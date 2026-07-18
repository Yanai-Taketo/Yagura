using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;

namespace Yagura.Host.Configuration;

/// <summary>
/// 設定ファイルのライブ再読み込みの実体（configuration.md §3。CF-4 層1。Issue #262）。
/// </summary>
/// <remarks>
/// <para>
/// <b>差分適用</b>: 前回適用時点の生 options（起動時はファイルスナップショット）と現在の
/// ファイル内容を <see cref="ConfigurationChangePlanner"/> で比較し、変更キーのうち
/// 即時反映の口を持つもの（コンストラクタで注入される <see cref="ImmediateConfigurationApplier"/>
/// 群がキーを宣言する）だけを適用する。無関係なコンポーネントには触れない（§3「差分適用」）。
/// </para>
/// <para>
/// <b>未反映の明示</b>: 変更されたが適用の口がないキー（再起動・層2 のリスナ再構成を要する
/// キー、および即時目標だが未実装のキー）は <b>再起動まで累積して</b>報告し続ける
/// （<see cref="ConfigurationReloadResult.PendingRestartKeys"/> + イベント ID 1020 の警告）。
/// 「設定した = 反映された」という前提が静かに崩れた状態を、次の再読み込みで見えなくしない。
/// 累積集合は <see cref="GetPendingRestartKeys"/> により再読み込み操作を伴わず読め、
/// 認証済み管理面の常設表示（Issue #286）がこれを表示する。
/// </para>
/// <para>
/// <b>検証失敗時は旧設定で継続</b>: 起動失敗分類の不正値（受信ポート不正等）は、起動時なら
/// fail-fast だが、稼働中の再読み込みでは適用を拒否して旧設定のまま継続する（イベント ID 1021）。
/// 「受信を止めない」（ADR-0002）を優先する意図的な非対称。
/// </para>
/// <para>
/// <b>監査</b>: 再読み込みは管理操作であり、実行（変更なしを除く）を
/// <see cref="AuditEventKind.ConfigurationReloaded"/>（2016）として記録する（§3。UI 経由・
/// SCM カスタム制御コード経由（CF-5）が同じ証跡に合流する）。
/// </para>
/// </remarks>
public sealed class ConfigurationReloadService : IConfigurationReloadService
{
    private readonly string _dataRoot;
    private readonly IAuditRecorder _auditRecorder;
    private readonly ILogger<ConfigurationReloadService> _logger;
    private readonly IReadOnlyList<ImmediateConfigurationApplier> _appliers;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private YaguraConfigurationOptions _lastAppliedOptions;

    // キー → 最初に検出した再読み込みの時刻（Issue #286——常設表示に検出時刻を併記する）。
    // 書き込みは _reloadGate 内だが、GetPendingRestartKeys は管理画面の描画からゲート外で
    // 呼ばれるため、辞書自体の整合はロックで守る。
    private readonly Dictionary<string, DateTimeOffset> _pendingRestartKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="dataRoot">データルートの絶対パス。</param>
    /// <param name="startupOptions">
    /// 起動時に読み込まれた生 options のスナップショット（差分計算の初期基準。
    /// <see cref="YaguraConfigurationWriter.Read"/> で捕捉する）。
    /// </param>
    /// <param name="appliers">即時反映の口（キー集合と適用処理の組）。</param>
    /// <param name="auditRecorder">監査記録（2016）の記録先。</param>
    public ConfigurationReloadService(
        string dataRoot,
        YaguraConfigurationOptions startupOptions,
        IReadOnlyList<ImmediateConfigurationApplier> appliers,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null,
        ILogger<ConfigurationReloadService>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(startupOptions);
        ArgumentNullException.ThrowIfNull(appliers);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _dataRoot = dataRoot;
        _lastAppliedOptions = startupOptions;
        _appliers = appliers;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ConfigurationReloadService>.Instance;
    }

    /// <inheritdoc />
    public async Task<ConfigurationReloadResult> ReloadAsync(
        string? operatorAddress,
        string? authenticationScheme,
        string? authenticatedPrincipal,
        CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await ExecuteReloadAsync().ConfigureAwait(false);

            if (!result.Rejected && result.HasChanges)
            {
                // 再読み込みは管理操作——監査記録（2016。イベントログ併記込み）。記録失敗は
                // 再読み込み自体を妨げない（FileAuditRecorder の契約）。
                await _auditRecorder.RecordAsync(
                    new AuditEvent(
                        OccurredAt: _timeProvider.GetUtcNow(),
                        Kind: AuditEventKind.ConfigurationReloaded,
                        RemoteAddress: operatorAddress,
                        RemotePort: null,
                        Detail: BuildAuditDetail(result),
                        AuthenticationScheme: authenticationScheme,
                        AuthenticatedPrincipal: authenticatedPrincipal),
                    CancellationToken.None).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task<ConfigurationReloadResult> ExecuteReloadAsync()
    {
        // ファイルの生 options（差分計算の after 側）。検証済みの実効値は Load が別途作る。
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);

        ConfigurationLoadResult loadResult;
        try
        {
            loadResult = YaguraConfigurationLoader.Load(_dataRoot, _logger);
        }
        catch (ConfigurationValidationException ex)
        {
            // 検証失敗——何も適用せず旧設定のまま継続する（クラス remarks 参照）。
            _logger.LogWarning(
                ConfigurationEventIds.ConfigurationReloadRejected,
                "設定の再読み込みを拒否しました（検証失敗）。実行中の構成は変更前のまま継続します: {Reason}",
                ex.Message);

            return new ConfigurationReloadResult(
                Rejected: true,
                RejectionReason: ex.Message,
                ChangedKeys: [],
                AppliedKeys: [],
                PendingRestartKeys: PendingRestartKeySnapshot(),
                WarningMessages: [],
                UnknownKeys: []);
        }

        var plan = ConfigurationChangePlanner.Compare(_lastAppliedOptions, snapshot.Options);

        var appliedKeys = new List<string>();
        foreach (var applier in _appliers)
        {
            if (plan.ChangedKeys.Any(key => applier.Keys.Contains(key, StringComparer.OrdinalIgnoreCase)))
            {
                await applier.ApplyAsync(loadResult.Configuration).ConfigureAwait(false);
                appliedKeys.AddRange(plan.ChangedKeys.Where(
                    key => applier.Keys.Contains(key, StringComparer.OrdinalIgnoreCase)));
            }
        }

        // 適用の口がなかった変更キーは「再起動待ち」として累積する（再起動まで見え続ける）。
        // 検出時刻は最初に検出した再読み込みの時刻で固定する（Issue #286——「いつから
        // 未反映のまま残っているか」を常設表示に併記する）。
        var detectedAt = _timeProvider.GetUtcNow();
        lock (_pendingRestartKeys)
        {
            foreach (var key in plan.ChangedKeys.Except(appliedKeys, StringComparer.OrdinalIgnoreCase))
            {
                _pendingRestartKeys.TryAdd(key, detectedAt);
            }
        }

        // 基準スナップショットは全体を差し替える（未反映キーは _pendingRestartKeys が
        // 追跡し続けるため、差分の再検出には依存しない）。
        _lastAppliedOptions = snapshot.Options;

        var pending = PendingRestartKeySnapshot();
        var warnings = loadResult.Warnings
            .Select(w => $"{w.Key}: {w.Reason}（適用値: {w.AppliedValue}）")
            .ToArray();

        if (pending.Length > 0)
        {
            _logger.LogWarning(
                ConfigurationEventIds.ConfigurationReloadPendingRestart,
                "設定の再読み込みを実行しましたが、次のキーは反映にサービス再起動が必要なため未反映のまま残っています: {PendingKeys}",
                string.Join(", ", pending));
        }

        return new ConfigurationReloadResult(
            Rejected: false,
            RejectionReason: null,
            ChangedKeys: plan.ChangedKeys,
            AppliedKeys: appliedKeys,
            PendingRestartKeys: pending,
            WarningMessages: warnings,
            UnknownKeys: loadResult.UnknownKeys);
    }

    /// <inheritdoc />
    public IReadOnlyList<PendingRestartKey> GetPendingRestartKeys()
    {
        lock (_pendingRestartKeys)
        {
            return _pendingRestartKeys
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new PendingRestartKey(pair.Key, pair.Value))
                .ToArray();
        }
    }

    private string[] PendingRestartKeySnapshot()
    {
        lock (_pendingRestartKeys)
        {
            return _pendingRestartKeys.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private static string BuildAuditDetail(ConfigurationReloadResult result)
    {
        var applied = result.AppliedKeys.Count > 0 ? string.Join(", ", result.AppliedKeys) : "(なし)";
        var pending = result.PendingRestartKeys.Count > 0 ? string.Join(", ", result.PendingRestartKeys) : "(なし)";
        return $"設定の再読み込み: 変更キー={string.Join(", ", result.ChangedKeys)} / 適用={applied} / 再起動待ち={pending}";
    }
}

/// <summary>
/// 即時反映の口（CF-4 層1）: 担当キー集合と、新しい検証済み設定を実行中のコンポーネントへ
/// 反映する処理の組。Program（合成ルート）が各コンポーネントの更新メソッドを束ねて登録する。
/// </summary>
/// <param name="Keys">担当する設定キー（JSON キーパス）。</param>
/// <param name="ApplyAsync">
/// 検証済みの新設定を反映する処理（担当キーのいずれかが変更されたときのみ呼ばれる）。
/// 層2（リスナ再構成——瞬断の記録を含む非同期処理）に合わせて非同期契約とする。
/// </param>
public sealed record ImmediateConfigurationApplier(
    IReadOnlyCollection<string> Keys,
    Func<ResolvedYaguraConfiguration, Task> ApplyAsync)
{
    /// <summary>同期処理向けの補助コンストラクタ（層1 の単純な参照交換系 applier 用）。</summary>
    public ImmediateConfigurationApplier(
        IReadOnlyCollection<string> keys,
        Action<ResolvedYaguraConfiguration> apply)
        : this(keys, resolved => { apply(resolved); return Task.CompletedTask; })
    {
    }
}
