using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Abstractions.Auditing;

namespace Yagura.Host.Configuration;

/// <summary>
/// サービス実行アカウントの証跡化（ADR-0015 決定 8。Issue #263。security.md §4.1・§4.3、
/// configuration.md §4.4）:
/// ①インストーラが書く構成記録（<c>service-account.ini</c>）の初回起動時のイベントログ転記
/// （監査 2024。<see cref="Firewall.FirewallStartupInspector"/> の 2017 と同型のインストーラ由来
/// 転記レール）②起動時に実効実行アカウント（プロセスが実際に動いている識別）を前回起動時の
/// 記録（<c>last-service-account.json</c>）と照合し、変化を監査 2025 として記録する。
/// </summary>
/// <remarks>
/// <para>
/// <b>変化検出（2025）が塞ぐ穴</b>: 製品外の <c>sc config</c> による実行アカウント切替
/// （ADR-0015 決定 6 の稼働中切替手順）は、どの管理操作の監査にも乗らない。実効アカウントを
/// 毎起動で記録・照合することで、この経路も次回起動で必ず証跡化され、「いつからこの識別で
/// 動いているか」に監査記録で答えられる（security.md §4.1）。
/// </para>
/// <para>
/// <b>改竄耐性の水準</b>: <c>last-service-account.json</c> は <c>last-applied-configuration.json</c>
/// と同水準——サービスアカウント書き込み可（実行アカウントを切り替えられる者＝管理者は消せる）
/// であり、悪意への統制ではなく事故調査のための運用証跡である。一次の耐タンパ線は 2024/2025 の
/// イベントログ併記（security.md §4.2。FileAuditRecorder の多段が担う）。
/// </para>
/// <para>
/// <b>旧アカウント ACE の残置チェックは限定的な補完</b>: security.md §5.2 の「アカウント切替時は
/// 旧アカウントの ACE を除去する」の除去責務はインストーラ（icacls 後追い）にあり、§5 の起動時
/// ACL 検証（期待 ACE 全体の突合・能動通知）は本クラスの責務ではない（SEC-D2 とあわせて別途）。
/// 本クラスは変化を検出した起動時に限り、データルートに旧アカウントの ACE が残っていないかを
/// ベストエフォートで確認し、残置を警告する——「残置が起きた実機で運用者が気づける経路」
/// （§5.2）の最小実装。旧アカウントが AD から削除済みで名前解決できない場合は照合できない
/// （その旨を情報ログに残す）。
/// </para>
/// <para>
/// <b>失敗は起動を妨げない</b>: FirewallStartupInspector / StartupConfigurationInspector と同型
/// （Build 後の手動呼び出し・例外は内部で完結。監査レール——IAuditRecorder——は例外を投げない契約）。
/// </para>
/// </remarks>
public sealed class ServiceAccountStartupInspector
{
    /// <summary>インストーラが書く構成記録のファイル名（installer/Package.wxs の ServiceAccountInstallRecord）。</summary>
    public const string InstallationRecordFileName = "service-account.ini";

    /// <summary>転記済みマーカーのファイル名（FirewallStartupInspector と同じ一回性判定方式）。</summary>
    public const string TranscribedMarkerFileName = "service-account.ini.transcribed";

    /// <summary>前回起動時の実効実行アカウント記録のファイル名（security.md §4.1）。</summary>
    public const string LastAccountFileName = "last-service-account.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _dataRoot;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ServiceAccountStartupInspector> _logger;

    public ServiceAccountStartupInspector(
        string dataRoot,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null,
        ILogger<ServiceAccountStartupInspector>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _dataRoot = dataRoot;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ServiceAccountStartupInspector>.Instance;
    }

    /// <summary>
    /// 現在の実効実行アカウント名（プロセスが実際に動いている Windows 識別）。秘密鍵権限の
    /// 付与先（security.md §5.2「付与先を実行アカウントから導出する」）と 2025 の照合対象に使う。
    /// 非 Windows 分岐は CA1416 ガード（本製品は Windows 専用——ADR-0001。
    /// <c>PromotionWizardService.ResolveCurrentAccountName</c> と同じ形）。
    /// </summary>
    public static string ResolveEffectiveAccountName() =>
        OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().Name
            : Environment.UserName;

    /// <summary>
    /// インストーラの構成記録（service-account.ini）を初回起動時に 1 回だけイベントログへ転記する
    /// （監査 2024。ADR-0015 決定 8——構成された実行アカウント名を記録する。gMSA 名は識別子であり
    /// 秘密ではない）。ini が無い（手動配置・MSI 以外の導入）場合は何もしない。失敗は起動を妨げない。
    /// </summary>
    public async Task TranscribeInstallationRecordOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var iniPath = Path.Combine(_dataRoot, InstallationRecordFileName);
            var markerPath = Path.Combine(_dataRoot, TranscribedMarkerFileName);

            if (!File.Exists(iniPath) || File.Exists(markerPath))
            {
                return;
            }

            var lines = await File.ReadAllLinesAsync(iniPath, cancellationToken).ConfigureAwait(false);
            var contentSummary = string.Join(" | ", lines
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('[') && !l.StartsWith(';')));

            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: _timeProvider.GetUtcNow(),
                    Kind: AuditEventKind.ServiceAccountTranscribed,
                    RemoteAddress: null,
                    RemotePort: null,
                    Detail: $"サービス実行アカウントの構成記録（{InstallationRecordFileName}）の転記: {contentSummary}"),
                CancellationToken.None).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                markerPath,
                $"transcribed at {_timeProvider.GetUtcNow():O}\n",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "サービス実行アカウントの構成記録（service-account.ini）の転記に失敗しました（次回起動時に再試行されます）。");
        }
    }

    /// <summary>
    /// 実効実行アカウントを前回起動時の記録と照合し（変化があれば監査 2025）、照合の結果に
    /// よらず今回の実効アカウントを新しい基準として保存する（2019 の設定差分照合と同じ
    /// 「変化検出」レール——検出済みの変化を次回起動で重複報告しない）。初回起動・記録欠損時は
    /// 照合をスキップし保存のみ行う（ADR-0015 決定 8「初回起動・記録欠損時は転記（2024）のみ」）。
    /// </summary>
    /// <param name="effectiveAccountName">
    /// 現在の実効実行アカウント（<see cref="ResolveEffectiveAccountName"/>。Program が起動時に解決する）。
    /// </param>
    public async Task DetectAccountChangeAndRefreshAsync(
        string effectiveAccountName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveAccountName);

        try
        {
            var previous = TryReadLastAccount();
            if (previous is null)
            {
                _logger.LogInformation(
                    "前回起動時のサービス実行アカウント記録（{FileName}）が存在しないため、変化の照合をスキップしました" +
                    "（初回起動、または記録の欠損・破損）。今回の実効アカウント {Account} を基準として保存します。",
                    LastAccountFileName,
                    effectiveAccountName);
            }
            else if (!string.Equals(previous, effectiveAccountName, StringComparison.OrdinalIgnoreCase))
            {
                await _auditRecorder.RecordAsync(
                    new AuditEvent(
                        OccurredAt: _timeProvider.GetUtcNow(),
                        Kind: AuditEventKind.ServiceAccountChangeDetected,
                        RemoteAddress: null,
                        RemotePort: null,
                        Detail: "起動時照合: サービス実行アカウントが前回起動時から変化しました。" +
                            $"旧={previous}、新={effectiveAccountName}" +
                            "（再インストール/アップグレードでの指定変更のほか、製品外の sc config による切替も" +
                            "本記録で証跡化される——ADR-0015 決定 8。security.md §4.1）"),
                    CancellationToken.None).ConfigureAwait(false);

                WarnIfOldAccountAceRemains(previous, effectiveAccountName);
            }

            TrySaveLastAccount(effectiveAccountName);
        }
        catch (Exception ex)
        {
            // 起動時の fire-and-forget 呼び出しで未観測例外を作らない最終ガード
            // （StartupConfigurationInspector と同型）。
            _logger.LogWarning(ex, "サービス実行アカウントの変化照合に失敗しました（起動は継続します）。");
        }
    }

    /// <summary>前回記録の実効アカウント名。欠損・破損は null（照合スキップの安全側）。</summary>
    private string? TryReadLastAccount()
    {
        var path = Path.Combine(_dataRoot, LastAccountFileName);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<ServiceAccountSnapshot>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(snapshot?.AccountName) ? null : snapshot.AccountName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex,
                "前回起動時のサービス実行アカウント記録（{FileName}）を読み取れませんでした（照合をスキップし、今回の値で保存し直します）。",
                LastAccountFileName);
            return null;
        }
    }

    private void TrySaveLastAccount(string accountName)
    {
        var path = Path.Combine(_dataRoot, LastAccountFileName);

        try
        {
            var snapshot = new ServiceAccountSnapshot(accountName, _timeProvider.GetUtcNow());
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex,
                "サービス実行アカウント記録（{FileName}）の保存に失敗しました（次回起動時の照合は初回起動と同じ扱いになります）。",
                LastAccountFileName);
        }
    }

    /// <summary>
    /// アカウント変化を検出した起動時に限り、データルートに旧アカウントの ACE が残っていないかを
    /// ベストエフォートで確認する（remarks「旧アカウント ACE の残置チェック」参照。security.md §5.2）。
    /// 照合できない（非 Windows・旧アカウントが名前解決できない・ACL 読み取り不可）場合は
    /// 情報ログに留め、起動へは影響させない。
    /// </summary>
    private void WarnIfOldAccountAceRemains(string oldAccountName, string newAccountName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            SecurityIdentifier oldSid;
            try
            {
                oldSid = (SecurityIdentifier)new NTAccount(oldAccountName).Translate(typeof(SecurityIdentifier));
            }
            catch (IdentityNotMappedException)
            {
                // 旧アカウントが AD から削除済み等で SID へ解決できない——ACE 側も名前へ解決されない
                // 孤児 SID として残っている可能性があるが、本チェックでは突き合わせられない
                // （全量の突合は §5 の起動時 ACL 検証——SEC-D2——の領分）。
                _logger.LogInformation(
                    "旧サービス実行アカウント {OldAccount} を SID へ解決できないため、データルートの残置 ACE の確認をスキップしました" +
                    "（アカウントが削除済みの場合、残置 ACE は icacls で孤児 SID として確認・除去できます——security.md §5.2）。",
                    oldAccountName);
                return;
            }

            var rules = new DirectoryInfo(_dataRoot)
                .GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));

            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.IdentityReference is SecurityIdentifier sid && sid.Equals(oldSid))
                {
                    _logger.LogWarning(
                        "[service-account-old-ace-remains] サービス実行アカウントは {NewAccount} へ切り替わりましたが、" +
                        "データルート {DataRoot} に旧アカウント {OldAccount} の ACE が残っています。旧識別が保存データへ" +
                        "アクセスできる状態を残さないため、icacls で旧アカウントの ACE を除去してください" +
                        "（security.md §5.2・docs/guides/gmsa-service-account.md の切替手順参照）。",
                        newAccountName,
                        _dataRoot,
                        oldAccountName);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            _logger.LogInformation(ex,
                "データルートの旧アカウント ACE の残置確認に失敗しました（確認は省略されます。起動には影響しません）。");
        }
    }

    /// <summary><c>last-service-account.json</c> の永続形。</summary>
    private sealed record ServiceAccountSnapshot(string AccountName, DateTimeOffset RecordedAtUtc);
}
