using System.Globalization;
using System.Net;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration;
using Yagura.Host.Configuration;
using Yagura.Storage;

namespace Yagura.Host.Observability.ActiveNotification.SourceSilence;

/// <summary>
/// <see cref="ISourceSilenceAdminService"/> の実体（ADR-0018 決定 4・5・6。Issue #351）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Email.EmailNotificationAdminService"/> と同じ 3 層構成に従う——正規化 →
/// fail-closed 検証 → 読み込み → 差分計算 → 差分なしなら no-op → 複製・変更 →
/// 楽観競合つき保存 → 監査 → 即時反映。
/// </para>
/// <para>
/// <b>保存時の検証を起動時より厳しくする理由も同じ</b>: 起動時
/// （<see cref="YaguraConfigurationLoader"/>）は不正エントリを「当該エントリのみ無効化 + 警告」に
/// 倒すが、保存時は利用者が目の前にいる——黙って監視から外れる構成を保存させるより、その場で
/// 理由を示して拒否するほうが原因に近い。UI では保存できないが手編集なら（縮退つきで）動く、
/// という非対称は既存の整理（手編集は監査対象外——security.md §4.1）の範囲内。
/// </para>
/// </remarks>
public sealed class SourceSilenceAdminService : ISourceSilenceAdminService
{
    /// <summary>候補取得のクエリ時間上限（画面の応答性を優先し、超過は例外で画面側に見せる）。</summary>
    private static readonly TimeSpan CandidateQueryTimeout = TimeSpan.FromSeconds(5);

    private readonly string _dataRoot;
    private readonly IAuditRecorder _auditRecorder;
    private readonly ILogStore _logStore;
    private readonly Action<IReadOnlyList<SourceSilenceWatchEntry>?> _applyToRuntime;
    private readonly Func<IReadOnlyList<Yagura.Abstractions.Observability.YaguraSourceSilenceReading>> _runtimeStates;
    private readonly TimeProvider _timeProvider;

    /// <remarks>
    /// 構築の口は <c>internal</c>（引数に internal 型——<see cref="SourceSilenceWatchEntry"/>——が
    /// 現れるため）。型自体は公開契約の実体として <c>public</c> を保つ
    /// （<see cref="Email.EmailNotificationAdminService"/> と同じ扱い）。
    /// </remarks>
    /// <param name="applyToRuntime">
    /// 保存後の即時反映の口（決定 6）。合成ルートが追跡器と判定器の
    /// <c>ApplyWatchlist</c> を束ねた同一のデリゲート（<c>ImmediateConfigurationApplier</c> に
    /// 渡すものと同じ実体）を渡す——反映経路を 1 本に保ち、UI 保存と再読み込みで挙動が
    /// 食い違わないようにする。
    /// </param>
    /// <param name="runtimeStates">
    /// 稼働中の判定状態の読み取り口（<see cref="SourceSilenceDetector.SnapshotEntryStatuses"/>）。
    /// </param>
    internal SourceSilenceAdminService(
        string dataRoot,
        IAuditRecorder auditRecorder,
        ILogStore logStore,
        Action<IReadOnlyList<SourceSilenceWatchEntry>?> applyToRuntime,
        Func<IReadOnlyList<Yagura.Abstractions.Observability.YaguraSourceSilenceReading>> runtimeStates,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(auditRecorder);
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(applyToRuntime);
        ArgumentNullException.ThrowIfNull(runtimeStates);

        _dataRoot = dataRoot;
        _auditRecorder = auditRecorder;
        _logStore = logStore;
        _applyToRuntime = applyToRuntime;
        _runtimeStates = runtimeStates;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<SourceSilenceAdminStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        return Task.FromResult(ToStatus(snapshot.Options));
    }

    public async Task<IReadOnlyList<SourceSilenceCandidate>> GetCandidatesAsync(
        int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // 候補選択では「いま送ってきている送信元」を上に出すのが目的（実在確認済みのアドレスから
        // 選ぶ。ADR-0018 決定 4）。QuerySourceActivityAsync（無音化検出用の昇順）を後から並べ替える
        // だけでは、送信元数が limit を超える環境で現役の送信元が DB 側の打ち切りで既に落ちている
        // （Issue #383）——DB 側で新しい順に打ち切る QueryMostRecentlyActiveSourcesAsync を使う。
        var activities = await _logStore
            .QueryMostRecentlyActiveSourcesAsync(limit, CandidateQueryTimeout, cancellationToken)
            .ConfigureAwait(false);

        var registered = new HashSet<string>(
            ReadRawWatchlist(YaguraConfigurationWriter.Read(_dataRoot).Options)
                .Select(item => Trimmed(item.Address))
                .Where(address => address is not null)
                .Select(address => TryNormalizeAddress(address!, out var normalized) ? normalized : address!),
            StringComparer.OrdinalIgnoreCase);

        return [.. activities
            .OrderByDescending(activity => activity.LastReceivedAt)
            .Select(activity => new SourceSilenceCandidate(
                activity.SourceAddress,
                activity.LastReceivedAt,
                activity.RecordCount,
                AlreadyRegistered: TryNormalizeAddress(activity.SourceAddress, out var normalized)
                    && registered.Contains(normalized)))];
    }

    public async Task<SourceSilenceConfigureResult> ConfigureAsync(
        SourceSilenceSettings settings,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Watchlist);

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var before = snapshot.Options;
        var beforeItems = ReadRawWatchlist(before);
        var beforeByAddress = IndexByNormalizedAddress(beforeItems);

        var normalized = Normalize(settings.Watchlist, beforeByAddress);

        // 差分（正規化アドレス単位）。追加・削除・変更のいずれもなければ no-op——
        // 同値保存の反復で監査証跡を希釈しない（メール通知設定と同じ判断）。
        var afterByAddress = normalized.ToDictionary(item => item.NormalizedAddress, StringComparer.OrdinalIgnoreCase);

        var added = normalized
            .Where(item => !beforeByAddress.ContainsKey(item.NormalizedAddress))
            .Select(item => Describe(item.NormalizedAddress, item.Label))
            .ToList();
        var removed = beforeByAddress
            .Where(pair => !afterByAddress.ContainsKey(pair.Key))
            .Select(pair => Describe(pair.Key, Trimmed(pair.Value.Label)))
            .ToList();
        var changed = normalized
            .Where(item =>
                beforeByAddress.TryGetValue(item.NormalizedAddress, out var beforeItem)
                && (!string.Equals(Trimmed(beforeItem.Label), item.Label, StringComparison.Ordinal)
                    || ParseThreshold(beforeItem.ThresholdMinutes) != item.ThresholdMinutes))
            .Select(item => Describe(item.NormalizedAddress, item.Label))
            .ToList();

        if (added.Count == 0 && removed.Count == 0 && changed.Count == 0)
        {
            return new SourceSilenceConfigureResult([], [], [], ToStatus(before));
        }

        var after = YaguraConfigurationOptionsCloner.Clone(before);
        after.Notification ??= new YaguraConfigurationOptions.NotificationOptions();
        after.Notification.SourceSilence ??= new YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions();
        after.Notification.SourceSilence.Watchlist = [.. normalized.Select(item =>
            new YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions.WatchlistEntryOptions
            {
                Address = item.NormalizedAddress,
                Label = item.Label,
                ThresholdMinutes = item.ThresholdMinutes?.ToString(CultureInfo.InvariantCulture),
            })];

        // 楽観競合（configuration.md §3）は ConfigurationConflictException をそのまま伝播する。
        YaguraConfigurationWriter.Save(_dataRoot, after, snapshot.VersionToken);

        // 監査 2023（決定 5）: 追加・削除・変更されたエントリのアドレスと表示名を必ず残す——
        // ウォッチリストは検知範囲そのものの定義であり、「管理権限を得た攻撃者が証跡遮断の前に
        // エントリを外す」を事後に再構成できる粒度が要る。値は秘密情報ではない。
        await _auditRecorder.RecordAsync(
            new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.SourceSilenceWatchlistConfigured,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail:
                    $"added=[{string.Join(", ", added)}] " +
                    $"removed=[{string.Join(", ", removed)}] " +
                    $"changed=[{string.Join(", ", changed)}] " +
                    $"total={normalized.Count}",
                AuthenticationScheme: operatorScheme,
                AuthenticatedPrincipal: operatorPrincipal),
            CancellationToken.None).ConfigureAwait(false);

        // 即時反映（決定 6）。検証・正規化・既定値補完の判断はすべて Loader に委ねる——
        // UI 経由と起動時・再読み込みで解決結果が食い違う余地を作らない
        // （EmailNotificationAdminService.ApplyToDispatcher と同じ判断）。
        var loaded = YaguraConfigurationLoader.Load(
            _dataRoot, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        _applyToRuntime(loaded.Configuration.SourceSilence?.Watchlist);

        return new SourceSilenceConfigureResult(
            AddedAddresses: added,
            RemovedAddresses: removed,
            ChangedAddresses: changed,
            Status: ToStatus(after));
    }

    /// <summary>正規化・検証済みの 1 エントリ。</summary>
    private sealed record NormalizedItem(string NormalizedAddress, string? Label, int? ThresholdMinutes);

    /// <summary>
    /// 入力を正規化し、保存を拒否すべき問題（クラス remarks）を検証する。
    /// </summary>
    private static List<NormalizedItem> Normalize(
        IReadOnlyList<SourceSilenceWatchlistItem> items,
        IReadOnlyDictionary<string, YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions.WatchlistEntryOptions> beforeByAddress)
    {
        if (items.Count > SourceSilenceConstants.MaxWatchlistEntries)
        {
            throw new WizardValidationException(
                $"ウォッチリストの登録は {SourceSilenceConstants.MaxWatchlistEntries} 件までです" +
                $"（現在 {items.Count} 件）。監視対象を見直して減らしてください。");
        }

        var result = new List<NormalizedItem>(items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var rawAddress = Trimmed(item.Address);
            if (rawAddress is null || !TryNormalizeAddress(rawAddress, out var address))
            {
                throw new WizardValidationException(
                    $"送信元アドレス「{item.Address}」が IP アドレスとして正しい形式ではありません。");
            }

            if (!seen.Add(address))
            {
                throw new WizardValidationException(
                    $"送信元アドレス「{address}」が重複しています。同じアドレスは 1 件にまとめてください" +
                    "（IPv4 と IPv4-mapped IPv6 の表記は同じアドレスとして扱います）。");
            }

            if (item.ThresholdMinutes is { } threshold
                && (threshold < SourceSilenceConstants.MinThresholdMinutes
                    || threshold > SourceSilenceConstants.MaxThresholdMinutes))
            {
                throw new WizardValidationException(
                    $"「{address}」の閾値は {SourceSilenceConstants.MinThresholdMinutes}〜" +
                    $"{SourceSilenceConstants.MaxThresholdMinutes} 分の範囲で指定してください。");
            }

            // UI 経由の新規登録は閾値の明示確定を必須とする（決定 4）。既存エントリ
            // （手編集で閾値を省略して保存されているもの）は省略のまま保持できる——
            // 省略の解消を、無関係な編集のたびに強要しない。
            if (item.ThresholdMinutes is null && !beforeByAddress.ContainsKey(address))
            {
                throw new WizardValidationException(
                    $"新しく登録する「{address}」の閾値を指定してください（既定 24 時間の自動補完は" +
                    "手編集専用です——「登録した = すぐ気づける」という期待とのズレを登録時に確認する" +
                    "ため、画面からの登録では閾値の明示を必須としています）。");
            }

            result.Add(new NormalizedItem(address, Trimmed(item.Label), item.ThresholdMinutes));
        }

        return result;
    }

    private SourceSilenceAdminStatus ToStatus(YaguraConfigurationOptions options)
    {
        var raw = ReadRawWatchlist(options);
        var defaultThreshold = ParseThreshold(options.Notification?.SourceSilence?.DefaultThresholdMinutes)
            ?? SourceSilenceConstants.DefaultThresholdMinutes;

        return new SourceSilenceAdminStatus(
            DefaultThresholdMinutes: defaultThreshold,
            MaxWatchlistEntries: SourceSilenceConstants.MaxWatchlistEntries,
            MinThresholdMinutes: SourceSilenceConstants.MinThresholdMinutes,
            MaxThresholdMinutes: SourceSilenceConstants.MaxThresholdMinutes,
            Watchlist: [.. raw.Select(entry => new SourceSilenceWatchlistItem(
                entry.Address ?? string.Empty,
                Trimmed(entry.Label),
                ParseThreshold(entry.ThresholdMinutes)))],
            RuntimeStates: _runtimeStates());
    }

    private static IReadOnlyList<YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions.WatchlistEntryOptions>
        ReadRawWatchlist(YaguraConfigurationOptions options) =>
        options.Notification?.SourceSilence?.Watchlist ?? [];

    private static Dictionary<string, YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions.WatchlistEntryOptions>
        IndexByNormalizedAddress(
            IReadOnlyList<YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions.WatchlistEntryOptions> items)
    {
        // 手編集で壊れた既存エントリ（アドレス不正・重複）は索引から黙って外さず、
        // 生値のまま重複しない範囲で保持する——差分計算は「正規化できるものは正規化キー、
        // できないものは生値キー」で行い、UI からの保存で不正エントリが「新規」として
        // 閾値必須の対象になることを避ける（不正アドレス自体は Normalize が拒否する）。
        var result = new Dictionary<string, YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions.WatchlistEntryOptions>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var raw = Trimmed(item.Address);
            if (raw is null)
            {
                continue;
            }

            var key = TryNormalizeAddress(raw, out var normalized) ? normalized : raw;
            result.TryAdd(key, item);
        }

        return result;
    }

    private static string Describe(string address, string? label) =>
        label is null ? address : $"{address}({label})";

    private static bool TryNormalizeAddress(string raw, out string normalized)
    {
        if (IPAddress.TryParse(raw, out var address))
        {
            normalized = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    private static int? ParseThreshold(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
