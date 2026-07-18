using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Abstractions.Auditing;

namespace Yagura.Host.Observability.Auditing;

/// <summary>
/// 拒否試行の集約記録（SEC-4。security.md §4.4。Issue #268）。<see cref="IAuditRecorder"/> の
/// デコレータとして全監査記録の下流に挟まり、同一送信元・同一種別の拒否が短時間に反復する場合に
/// 個別記録から集約記録（<see cref="AuditEventKind.RejectionAggregated"/>）へ切り替える。
/// </summary>
/// <remarks>
/// <para>
/// <b>目的</b>: 総当たり等の反復拒否が監査記録を洪水させ「証跡の希釈・ディスク圧迫」を起こすことを
/// 防ぐ（§4.4）。同時に <b>「破棄は必ず計上」と同じ思想で件数は失わない</b>——集約中も全件を数え、
/// サマリに回数と「試行された利用者名の集合」を残す（どのアカウントが狙われたかを件数に畳まない）。
/// </para>
/// <para>
/// <b>設計</b>:
/// <list type="bullet">
/// <item>集約キー = <c>(Kind, RemoteAddress)</c>。<b>事象種別が変われば別キー</b>＝別集約になり、
/// 種別横断で件数を畳まない（§4.4）。集約対象は <see cref="AuditAggregationDefaults.AggregatedKinds"/>
/// のみ（管理操作・グローバルバケット涸渇 3007 等は素通りで個別記録）。<c>RemoteAddress</c> が
/// 無い事象も集約せず素通りする。</item>
/// <item>窓 <see cref="AuditAggregationDefaults.AggregationWindow"/> 内に閾値
/// <see cref="AuditAggregationDefaults.AggregationThreshold"/> 回に達したら集約モードへ入る。
/// <b>閾値までは個別記録する</b>（単発・少数を希釈しない——§4.4 の判定基準）。</item>
/// <item>集約モード中の事象は個別記録せず、回数・利用者名集合・最終事象を更新する。</item>
/// <item>静穏窓 <see cref="AuditAggregationDefaults.QuietWindow"/> の間、新たな事象が来なければ
/// 集約サマリを 1 件（3012）記録し、キーを破棄して <b>個別記録へ復帰</b>する（§4.4。周期スキャン
/// <see cref="FlushStaleAsync"/> + プロセス停止時の全フラッシュで確実に出す）。</item>
/// </list>
/// </para>
/// <para>
/// <b>デコレータの透過性</b>: 内側（<see cref="FileAuditRecorder"/>）への委譲だけを行い、記録の
/// 実体（ファイル + イベントログ併記）は内側が担う。DI では本クラスを <see cref="IAuditRecorder"/>
/// として登録し内側に FileAuditRecorder を注入する——全呼び出し側に透過的に効く。
/// </para>
/// </remarks>
public sealed class AggregatingAuditRecorder : IAuditRecorder, IHostedService, IAsyncDisposable
{
    private static readonly Regex UsernamePattern =
        new(@"username=(?<u>\S+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IAuditRecorder _inner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AggregatingAuditRecorder> _logger;
    private readonly ConcurrentDictionary<AggregationKey, AggregationState> _states = new();
    private readonly object _sync = new();

    private ITimer? _flushTimer;

    public AggregatingAuditRecorder(
        IAuditRecorder inner,
        TimeProvider? timeProvider = null,
        ILogger<AggregatingAuditRecorder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AggregatingAuditRecorder>.Instance;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _flushTimer = _timeProvider.CreateTimer(
            _ => _ = FlushStaleAsync(),
            state: null,
            AuditAggregationDefaults.FlushScanInterval,
            AuditAggregationDefaults.FlushScanInterval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _flushTimer?.Dispose();
        _flushTimer = null;

        // 停止時は集約中の全キーをフラッシュする——未確定の集約を残さない（件数を失わない）。
        await FlushAllAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        // 集約対象でない（管理操作・グローバルバケット 3007 等）、または送信元が無い事象は素通り。
        if (!AuditAggregationDefaults.AggregatedKinds.Contains(auditEvent.Kind)
            || string.IsNullOrEmpty(auditEvent.RemoteAddress))
        {
            await _inner.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
            return;
        }

        var key = new AggregationKey(auditEvent.Kind, auditEvent.RemoteAddress);
        bool recordIndividually;

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();

            if (!_states.TryGetValue(key, out var state))
            {
                // 新規キー。個別記録しつつカウントを開始する。
                _states[key] = AggregationState.Started(auditEvent, now);
                recordIndividually = true;
            }
            else if (!state.IsAggregating && now - state.WindowStart >= AuditAggregationDefaults.AggregationWindow)
            {
                // 窓を跨いだ（間隔が空いた）——散発的な失敗として新しい窓でやり直す（個別記録）。
                _states[key] = AggregationState.Started(auditEvent, now);
                recordIndividually = true;
            }
            else
            {
                state.Add(auditEvent, now);

                if (!state.IsAggregating && state.Count >= AuditAggregationDefaults.AggregationThreshold)
                {
                    // 閾値到達——集約モードへ入る。以降は個別記録しない。
                    state.EnterAggregating();
                }

                recordIndividually = !state.IsAggregating;
            }
        }

        if (recordIndividually)
        {
            await _inner.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 静穏窓を経過した集約キーのサマリを記録し、キーを破棄する（個別記録へ復帰）。周期スキャンから
    /// 呼ばれる。例外は握って握りつぶさずログに留める（監査の集約が受信・他記録を妨げない）。
    /// </summary>
    internal async Task FlushStaleAsync()
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            List<AuditEvent> summaries = new();

            lock (_sync)
            {
                foreach (var (key, state) in _states)
                {
                    if (state.IsAggregating && now - state.LastSeen >= AuditAggregationDefaults.QuietWindow)
                    {
                        summaries.Add(state.BuildSummary(key));
                        _states.TryRemove(key, out _);
                    }
                }
            }

            foreach (var summary in summaries)
            {
                await _inner.RecordAsync(summary, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拒否試行の集約サマリのフラッシュに失敗しました（次回の周期で再試行されます）。");
        }
    }

    private async Task FlushAllAsync()
    {
        List<AuditEvent> summaries = new();
        lock (_sync)
        {
            foreach (var (key, state) in _states)
            {
                if (state.IsAggregating)
                {
                    summaries.Add(state.BuildSummary(key));
                }
            }

            _states.Clear();
        }

        foreach (var summary in summaries)
        {
            try
            {
                await _inner.RecordAsync(summary, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止時の集約サマリ記録に失敗しました。");
            }
        }
    }

    private readonly record struct AggregationKey(AuditEventKind Kind, string RemoteAddress);

    /// <summary>1 集約キーの状態（<see cref="_sync"/> 下でのみ触る）。</summary>
    private sealed class AggregationState
    {
        private readonly AuditEvent _firstEvent;
        private AuditEvent _lastEvent;
        private readonly HashSet<string> _usernames = new(StringComparer.Ordinal);

        private AggregationState(AuditEvent first, DateTimeOffset now)
        {
            _firstEvent = first;
            _lastEvent = first;
            WindowStart = now;
            LastSeen = now;
            Count = 1;
            TryAddUsername(first);
        }

        public DateTimeOffset WindowStart { get; }
        public DateTimeOffset LastSeen { get; private set; }
        public int Count { get; private set; }
        public bool IsAggregating { get; private set; }

        public static AggregationState Started(AuditEvent first, DateTimeOffset now) => new(first, now);

        public void Add(AuditEvent e, DateTimeOffset now)
        {
            _lastEvent = e;
            LastSeen = now;
            Count++;
            TryAddUsername(e);
        }

        public void EnterAggregating() => IsAggregating = true;

        private void TryAddUsername(AuditEvent e)
        {
            if (e.Detail is { } detail)
            {
                var match = UsernamePattern.Match(detail);
                if (match.Success && _usernames.Count < AuditAggregationDefaults.MaxDistinctUsernamesInSummary + 1)
                {
                    _usernames.Add(match.Groups["u"].Value);
                }
            }
        }

        public AuditEvent BuildSummary(AggregationKey key)
        {
            var listed = _usernames.Take(AuditAggregationDefaults.MaxDistinctUsernamesInSummary);
            var usernamesText = _usernames.Count == 0
                ? "(なし)"
                : string.Join(",", listed) +
                    (_usernames.Count > AuditAggregationDefaults.MaxDistinctUsernamesInSummary
                        ? $",...(+{_usernames.Count - AuditAggregationDefaults.MaxDistinctUsernamesInSummary})"
                        : string.Empty);

            var detail =
                $"aggregatedKind={key.Kind} count={Count} " +
                $"period={_firstEvent.OccurredAt:O}〜{_lastEvent.OccurredAt:O} " +
                $"usernames={usernamesText} " +
                $"firstDetail=[{_firstEvent.Detail}] lastDetail=[{_lastEvent.Detail}]";

            // 最初と最後のフル詳細（§4.1 と同粒度）は Detail に含め、集約サマリ自体は 3012 として
            // 送信元・到達ポート・試行パスは最後の事象のものを代表値に採る。
            return new AuditEvent(
                OccurredAt: _lastEvent.OccurredAt,
                Kind: AuditEventKind.RejectionAggregated,
                RemoteAddress: key.RemoteAddress,
                RemotePort: _lastEvent.RemotePort,
                AttemptedPath: _lastEvent.AttemptedPath,
                ReachedListenerPort: _lastEvent.ReachedListenerPort,
                Detail: detail,
                AuthenticationScheme: _lastEvent.AuthenticationScheme,
                AuthenticatedPrincipal: _lastEvent.AuthenticatedPrincipal);
        }
    }
}
