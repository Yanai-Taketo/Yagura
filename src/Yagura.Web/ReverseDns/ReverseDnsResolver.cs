using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Yagura.Web.Diagnostics;

namespace Yagura.Web.ReverseDns;

/// <summary>
/// <see cref="IReverseDnsResolver"/> の実装（ADR-0007 決定 2・3・4・5）。
/// キャッシュ充填型——描画は解決を待たず、キャッシュ命中分のみ即時表示し、
/// 未解決分は背景の解決完了後の束ね通知（<see cref="NamesUpdated"/>）で反映される。
/// </summary>
/// <remarks>
/// <para>
/// <b>配置の判断（ADR-0007 決定 2 が実装 PR へ委任した事項）</b>: 契約 <see cref="IReverseDnsResolver"/>
/// は <c>Yagura.Web</c> 内に置く（M6-4 の判断枠組み——Web の外から参照する利用者が存在しないため
/// <c>Yagura.Abstractions</c> へは置かない）。キャッシュのデータ構造は
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>（IP 文字列 → エントリ）+ 固定 TTL とする
/// （BCL は DNS レコードの TTL を取得できない——ADR-0007 帰結）。
/// </para>
/// <para>
/// <b>解決対象の帯域限定（ADR-0007 決定 2）</b>: ループバック・RFC 1918・RFC 6598（100.64/10）・
/// リンクローカル（169.254/16・fe80::/10）・ULA（fc00::/7）のみ解決する。対象帯域外
/// （グローバル等）はクエリ自体を発しない——「オフ時・対象帯域外で下位の解決 API へ到達しない」
/// ことは <c>ReverseDnsResolverTests</c> が固定する（security.md §1.1）。
/// </para>
/// </remarks>
public sealed class ReverseDnsResolver : IReverseDnsResolver, IDisposable
{
    private ReverseDnsDisplayOptions _options;
    private readonly IReverseDnsLookup _lookup;
    private readonly ReverseDnsMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ReverseDnsResolverLimits _limits;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);
    private int _activeLookups;
    private int _notifyPending;
    private volatile bool _disposed;

    private readonly record struct CacheEntry(string? Name, DateTimeOffset ExpiresAt);

    public ReverseDnsResolver(
        ReverseDnsDisplayOptions options,
        IReverseDnsLookup lookup,
        ReverseDnsMetrics metrics,
        TimeProvider timeProvider,
        ReverseDnsResolverLimits? limits = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _limits = limits ?? ReverseDnsResolverLimits.Default;
    }

    /// <inheritdoc />
    public event Action? NamesUpdated;

    /// <summary>
    /// 有効/無効を実行中に更新する（設定ライブ再読み込み。CF-4 層1。Issue #262）。
    /// 判定は呼び出しごとに <c>_options.Enabled</c> を読むため、参照の交換だけで
    /// 次の表示・解決要求から反映される（無効化してもキャッシュは消さない——再有効化時に
    /// 即座に表示が戻る。オフの間は表示・解決とも行わないため「オフ = 逆引き名は出ない」
    /// （ADR-0007 決定 4）は維持される）。
    /// </summary>
    public void UpdateOptions(ReverseDnsDisplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Volatile.Write(ref _options, options);
    }

    /// <inheritdoc />
    public string? TryGetDisplayName(string sourceAddress)
    {
        // オフ時は解決もキャッシュ参照表示も行わない（「オフ = 逆引き名は出ない」——ADR-0007 決定 4）。
        if (!_options.Enabled || _disposed || string.IsNullOrWhiteSpace(sourceAddress))
        {
            return null;
        }

        if (!IPAddress.TryParse(sourceAddress, out var address))
        {
            // IP として解釈できない値は解決対象にしない（キャッシュも汚さない）。
            return null;
        }

        // IPv4-mapped IPv6 は IPv4 へ正規化してから帯域判定・キー化する（ADR-0007 決定 2 の
        // 境界ケース——`::ffff:10.0.0.1` を 10.0.0.1 と同一に扱う）。
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (!IsResolvableAddress(address))
        {
            return null;
        }

        var key = address.ToString();
        var now = _timeProvider.GetUtcNow();

        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
        {
            return entry.Name;
        }

        ScheduleLookup(key, address);
        return null;
    }

    /// <summary>
    /// 解決対象の帯域か（ADR-0007 決定 2——プライベート/サイトローカル系のみ。対象帯域外は
    /// クエリを発しない）。
    /// </summary>
    internal static bool IsResolvableAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10                                   // 10/8（RFC 1918）
                || (b[0] == 172 && (b[1] & 0xF0) == 16)         // 172.16/12（RFC 1918）
                || (b[0] == 192 && b[1] == 168)                 // 192.168/16（RFC 1918）
                || (b[0] == 100 && (b[1] & 0xC0) == 64)         // 100.64/10（RFC 6598 共有アドレス空間）
                || (b[0] == 169 && b[1] == 254);                // 169.254/16（リンクローカル）
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal)                        // fe80::/10
            {
                return true;
            }

            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC;                       // fc00::/7（ULA）
        }

        return false;
    }

    /// <summary>
    /// ホスト名の無害化（ADR-0007 決定 5）: RFC 1123 の LDH（英数字・ハイフン・ドット）以外を
    /// 含む名前・253 文字超は表示しない（切り詰め・整形をして見せない）。`xn--`（Punycode）は
    /// LDH に適合するため原文のまま通る（Unicode への復号はしない）。
    /// </summary>
    internal static string? Sanitize(string? hostName)
    {
        if (string.IsNullOrEmpty(hostName))
        {
            return null;
        }

        // 完全修飾名の末尾ドットのみ許容して除去する（構造上の終端であり内容の整形ではない）。
        var candidate = hostName.EndsWith('.') ? hostName[..^1] : hostName;
        if (candidate.Length is 0 or > 253)
        {
            return null;
        }

        foreach (var label in candidate.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return null;
            }

            foreach (var c in label)
            {
                var isLdh = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-';
                if (!isLdh)
                {
                    return null;
                }
            }
        }

        return candidate;
    }

    private void ScheduleLookup(string key, IPAddress address)
    {
        if (_inFlight.ContainsKey(key))
        {
            return;
        }

        // キャッシュ件数の上限（ADR-0007 決定 3——検索結果は送信元詐称を含み得るため、
        // 外部入力でキャッシュを無制限に成長させない）。超過時は見送り = IP のみ表示。
        if (_cache.Count >= _limits.MaxCacheEntries)
        {
            _metrics.RecordSkipped();
            return;
        }

        // 同時解決数の上限（ADR-0007 決定 3）。到達時は延期——キューに積まず、次の表示更新の
        // TryGetDisplayName で自然に再試行される（ReverseDnsResolverLimits の説明参照）。
        if (Volatile.Read(ref _activeLookups) >= _limits.MaxConcurrentLookups)
        {
            return;
        }

        if (!_inFlight.TryAdd(key, Task.CompletedTask))
        {
            return;
        }

        Interlocked.Increment(ref _activeLookups);
        var task = ResolveAsync(key, address);

        // ResolveAsync の finally（TryRemove）が先行した場合は TryUpdate が失敗し、台帳は
        // 空のまま正しく終わる（完了済みタスクを再挿入して再解決を永久に塞ぐ競合を作らない）。
        _inFlight.TryUpdate(key, task, Task.CompletedTask);
    }

    private async Task ResolveAsync(string key, IPAddress address)
    {
        // 表示要求スレッド（Blazor の描画）を占有しないよう常にスレッドプールへ逃がす。
        await Task.Yield();

        try
        {
            var lookupTask = _lookup.QueryPtrAsync(address, CancellationToken.None);
            var completed = await Task.WhenAny(
                lookupTask,
                Task.Delay(_limits.LookupTimeout, _timeProvider)).ConfigureAwait(false);

            if (completed != lookupTask)
            {
                // 打ち切り = 「待つのをやめる」（OS の解決は中断できず完走する——ADR-0007 検証記録）。
                // 失敗として負のキャッシュへ入れ、完走した成功結果は事後に上書き反映する
                // （追加の計上はしない——1 回の解決は 1 回だけ数える）。
                _metrics.RecordFailed();
                Store(key, name: null, negative: true);
                _ = lookupTask.ContinueWith(
                    t =>
                    {
                        if (t.IsCompletedSuccessfully && Sanitize(t.Result) is { } lateName)
                        {
                            Store(key, lateName, negative: false);
                        }
                        else
                        {
                            // 遅延完走の失敗・未登録は観測（計上・上書き）しない——打ち切り時点で
                            // failed として計上済みであり、負のキャッシュも投入済みのため。
                            _ = t.Exception; // 未観測例外の finalizer 経路を塞ぐ
                        }
                    },
                    TaskScheduler.Default);
                return;
            }

            var rawName = await lookupTask.ConfigureAwait(false);
            if (rawName is null)
            {
                // PTR 未登録は正常系（ADR-0007 文脈 3）。
                _metrics.RecordNotFound();
                Store(key, name: null, negative: true);
                return;
            }

            // 解決自体は成功（resolved に計上）。無害化で表示不可となった名前は「名前なし」として
            // 正の TTL でキャッシュする（表示規約による非表示——ADR-0007 決定 5。
            // 解釈を偽装しないため切り詰め表示はしない）。
            _metrics.RecordResolved();
            Store(key, Sanitize(rawName), negative: false);
        }
        catch (SocketException)
        {
            // 未登録以外の解決失敗（DNS 障害等）。失敗の増加が異常のシグナル（ADR-0007 決定 6）。
            // 例外オブジェクトは logger へ渡さない（untrusted 値を証跡系へ持ち込まない——決定 5 ③）。
            _metrics.RecordFailed();
            Store(key, name: null, negative: true);
        }
        catch (Exception)
        {
            // 想定外の失敗も同じ縮退（IP のみ表示）。表示補助の失敗を上へ伝播させない。
            _metrics.RecordFailed();
            Store(key, name: null, negative: true);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
            Interlocked.Decrement(ref _activeLookups);
        }
    }

    private void Store(string key, string? name, bool negative)
    {
        var ttl = negative ? _limits.NegativeTtl : _limits.PositiveTtl;
        _cache[key] = new CacheEntry(name, _timeProvider.GetUtcNow() + ttl);
        RequestNotify();
    }

    private void RequestNotify()
    {
        // 反映の束ね（ADR-0007 決定 2）: 完了 1 件ごとに circuit 越しの再描画を発火させない。
        if (Interlocked.CompareExchange(ref _notifyPending, 1, 0) != 0)
        {
            return;
        }

        _ = NotifyAfterBatchIntervalAsync();
    }

    private async Task NotifyAfterBatchIntervalAsync()
    {
        try
        {
            if (_limits.NotifyBatchInterval > TimeSpan.Zero)
            {
                await Task.Delay(_limits.NotifyBatchInterval, _timeProvider).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _notifyPending, 0);
        }

        if (!_disposed)
        {
            NamesUpdated?.Invoke();
        }
    }

    /// <summary>進行中の解決の完了を待つ（テスト用——本番コードから呼び出さない）。</summary>
    internal async Task WaitForPendingLookupsAsync()
    {
        while (!_inFlight.IsEmpty)
        {
            await Task.WhenAll(_inFlight.Values.ToArray()).ConfigureAwait(false);
            await Task.Yield();
        }
    }

    public void Dispose() => _disposed = true;
}
