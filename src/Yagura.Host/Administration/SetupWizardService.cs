using System.Globalization;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Administration;

/// <summary>
/// 初期セットアップウィザードの実体（<see cref="ISetupWizardService"/>。M8-4。Issue #71）。
/// </summary>
/// <remarks>
/// <para>
/// <b>配置</b>: 設定ファイル（<c>yagura.json</c>）の読み書きは <c>Yagura.Host.Configuration</c>
/// の管轄（<see cref="YaguraConfigurationWriter"/>）であるため、実体は Host に置き、UI
/// （<c>Yagura.Web.Administration.Screens</c>）は <c>Yagura.Abstractions</c> の契約のみを
/// 参照する（architecture.md §1.1 の参照構造。<c>FileAuditRecorder</c> と同じ結線パターン）。
/// </para>
/// <para>
/// <b>セッション統治（configuration.md §7）</b>: 進行状態（確定済みステップ・入力値）は
/// 本シングルトンのメモリに保持する——circuit のメモリではなくサーバ側の状態であり、
/// circuit 喪失・再接続後の再入で確定済みステップから再開できる。サービス再起動をまたぐ
/// ファイル永続化は骨格に含めない（初期セットアップは分レベルで完了する操作であり、
/// 再起動をまたいで保持すべき資格情報も本ウィザードにはない。昇格の準備フェーズのような
/// 日をまたぐ中断・再開の要否は実利用を踏まえ CF-3 と合わせて評価する）。
/// </para>
/// <para>
/// <b>確定操作の一回性（§7）</b>: 適用は確認ステップ確定時に発行した冪等トークンを要求し、
/// 同一トークンの再送（瞬断 → 再送）では再適用しない。<b>「半分だけ適用」を作らない</b>——
/// 保存は <see cref="YaguraConfigurationWriter.Save"/> の原子的置換 1 回であり、
/// 検証 → 適用 → 記録が単一の確定単位になる。
/// </para>
/// </remarks>
public sealed class SetupWizardService : ISetupWizardService
{
    private static readonly SetupWizardStep[] StepOrder =
    [
        SetupWizardStep.Reception,
        SetupWizardStep.ViewerAccess,
        SetupWizardStep.Retention,
        SetupWizardStep.Review,
    ];

    private readonly object _gate = new();
    private readonly string _dataRoot;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;

    // ---- サーバ側セッション状態（configuration.md §7） ----
    private readonly List<SetupWizardStep> _confirmedSteps = [];
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private YaguraConfigurationFileSnapshot? _reviewSnapshot;
    private string? _applyToken;
    private string? _appliedToken;
    private SetupWizardApplyResult? _appliedResult;

    public SetupWizardService(string dataRoot, IAuditRecorder auditRecorder, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _dataRoot = dataRoot;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public Task<SetupWizardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(BuildSnapshot());
        }
    }

    /// <inheritdoc/>
    public Task<SetupWizardSnapshot> ConfirmStepAsync(
        SetupWizardStep step,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        lock (_gate)
        {
            if (_appliedResult is not null)
            {
                throw new WizardValidationException("設定は既に保存済みです。変更はやり直しではなく設定変更として行ってください。");
            }

            // 先のステップへ飛ぶことはできない（確定済みステップのやり直しは許す——
            // configuration.md §7 の「確定済みステップから再開」は前方修正を妨げない）。
            var nextStep = ComputeNextStep();
            if (step != nextStep && !_confirmedSteps.Contains(step))
            {
                throw new WizardValidationException("前のステップが未確定のため、このステップはまだ確定できません。");
            }

            switch (step)
            {
                case SetupWizardStep.Reception:
                    ValidatePort(values, SetupWizardValueKeys.UdpPort, "UDP 受信ポート");
                    ValidatePort(values, SetupWizardValueKeys.TcpPort, "TCP 受信ポート");
                    Store(values, SetupWizardValueKeys.UdpPort, SetupWizardValueKeys.TcpPort);
                    break;

                case SetupWizardStep.ViewerAccess:
                    ValidatePort(values, SetupWizardValueKeys.ViewerHttpPort, "閲覧ポート");
                    ValidatePort(values, SetupWizardValueKeys.AdminHttpPort, "管理ポート");
                    ValidatePublicAccess(values);
                    Store(values, SetupWizardValueKeys.ViewerHttpPort, SetupWizardValueKeys.ViewerPublicAccess, SetupWizardValueKeys.AdminHttpPort);
                    break;

                case SetupWizardStep.Retention:
                    ValidatePositiveInt(values, SetupWizardValueKeys.RetentionDays, "ログを保存しておく期間（日数）");
                    Store(values, SetupWizardValueKeys.RetentionDays);
                    break;

                case SetupWizardStep.Review:
                    // 「読み込み → 変更 → 検証 → 保存」（configuration.md §3）の「読み込み」。
                    // この時点のバージョントークンが適用時の楽観競合検出の基準になる。
                    _reviewSnapshot = YaguraConfigurationWriter.Read(_dataRoot);
                    _applyToken = Guid.NewGuid().ToString("N");
                    break;

                default:
                    throw new WizardValidationException("未知のステップです。");
            }

            if (!_confirmedSteps.Contains(step))
            {
                _confirmedSteps.Add(step);
            }

            return Task.FromResult(BuildSnapshot());
        }
    }

    /// <inheritdoc/>
    public async Task<SetupWizardApplyResult> ApplyAsync(
        string idempotencyToken,
        string? operatorAddress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyToken);

        SetupWizardApplyResult result;
        AuditEvent? auditEvent = null;

        lock (_gate)
        {
            // 一回性の保証（configuration.md §7）: 同一トークンの再送は再適用しない。
            if (_appliedToken is not null && string.Equals(idempotencyToken, _appliedToken, StringComparison.Ordinal))
            {
                return _appliedResult! with { Outcome = WizardApplyOutcome.AlreadyApplied };
            }

            if (_applyToken is null ||
                !string.Equals(idempotencyToken, _applyToken, StringComparison.Ordinal) ||
                _reviewSnapshot is null)
            {
                return new SetupWizardApplyResult(WizardApplyOutcome.InvalidToken, [], ConfigurationApplyEffect.Immediate);
            }

            var before = _reviewSnapshot.Options;
            var after = ApplyValues(before);
            var plan = ConfigurationChangePlanner.Compare(before, after);

            try
            {
                YaguraConfigurationWriter.Save(_dataRoot, after, _reviewSnapshot.VersionToken);
            }
            catch (ConfigurationConflictException)
            {
                // 楽観競合（§3）: 上書きせず、確認ステップからのやり直しを促す。
                _reviewSnapshot = null;
                _applyToken = null;
                _confirmedSteps.Remove(SetupWizardStep.Review);
                return new SetupWizardApplyResult(WizardApplyOutcome.Conflict, [], ConfigurationApplyEffect.Immediate);
            }

            result = new SetupWizardApplyResult(
                WizardApplyOutcome.Applied,
                plan.ChangedKeys,
                ToApplyEffect(plan.RequiredEffect));

            _appliedToken = idempotencyToken;
            _appliedResult = result;

            // 監査記録（security.md §4.1「設定変更」= 2000 番台 ID 2001）。本ウィザードの対象
            // キーに秘密情報は含まれないため、変更キーと前後値をそのまま要約に載せる。
            auditEvent = new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.ConfigurationSaved,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail: BuildAuditDetail(plan));
        }

        // 監査記録はロックの外で（IAuditRecorder は失敗しても例外を投げない契約——記録失敗が
        // 管理操作を妨げない。ADR-0004 決定 7）。
        await _auditRecorder.RecordAsync(auditEvent!, CancellationToken.None).ConfigureAwait(false);

        return result;
    }

    private SetupWizardSnapshot BuildSnapshot() => new(
        ConfirmedSteps: _confirmedSteps.ToList(),
        NextStep: ComputeNextStep(),
        ConfirmedValues: new Dictionary<string, string>(_values),
        ApplyIdempotencyToken: _applyToken,
        Applied: _appliedResult is not null);

    private SetupWizardStep ComputeNextStep() =>
        StepOrder.FirstOrDefault(step => !_confirmedSteps.Contains(step), SetupWizardStep.Review);

    private void Store(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            _values[key] = values[key].Trim();
        }
    }

    private static void ValidatePort(IReadOnlyDictionary<string, string> values, string key, string label)
    {
        if (!values.TryGetValue(key, out var raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            throw new WizardValidationException($"{label}は 1〜65535 の数値で入力してください。");
        }
    }

    private static void ValidatePositiveInt(IReadOnlyDictionary<string, string> values, string key, string label)
    {
        if (!values.TryGetValue(key, out var raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value < 1)
        {
            throw new WizardValidationException($"{label}は 1 以上の数値で入力してください。");
        }
    }

    private static void ValidatePublicAccess(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue(SetupWizardValueKeys.ViewerPublicAccess, out var raw) ||
            (!string.Equals(raw.Trim(), "Lan", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(raw.Trim(), "LocalhostOnly", StringComparison.OrdinalIgnoreCase)))
        {
            throw new WizardValidationException("公開範囲は Lan または LocalhostOnly を入力してください。");
        }
    }

    /// <summary>
    /// 確定済みの入力値を、読み込んだ生の設定（<paramref name="before"/>）へ全体書き換え用に
    /// 適用した新しいインスタンスを返す（元は変更しない——比較（ChangePlanner）の前提）。
    /// </summary>
    private YaguraConfigurationOptions ApplyValues(YaguraConfigurationOptions before)
    {
        var after = YaguraConfigurationOptionsCloner.Clone(before);

        after.Ingestion ??= new YaguraConfigurationOptions.IngestionOptions();
        after.Ingestion.Udp ??= new YaguraConfigurationOptions.IngestionOptions.UdpOptions();
        after.Ingestion.Tcp ??= new YaguraConfigurationOptions.IngestionOptions.TcpOptions();
        after.Viewer ??= new YaguraConfigurationOptions.ViewerOptions();
        after.Admin ??= new YaguraConfigurationOptions.AdminOptions();
        after.Retention ??= new YaguraConfigurationOptions.RetentionOptions();

        if (_values.TryGetValue(SetupWizardValueKeys.UdpPort, out var udpPort))
        {
            after.Ingestion.Udp.Port = udpPort;
        }

        if (_values.TryGetValue(SetupWizardValueKeys.TcpPort, out var tcpPort))
        {
            after.Ingestion.Tcp.Port = tcpPort;
        }

        if (_values.TryGetValue(SetupWizardValueKeys.ViewerHttpPort, out var viewerPort))
        {
            after.Viewer.HttpPort = viewerPort;
        }

        if (_values.TryGetValue(SetupWizardValueKeys.ViewerPublicAccess, out var publicAccess))
        {
            after.Viewer.PublicAccess = publicAccess;
        }

        if (_values.TryGetValue(SetupWizardValueKeys.AdminHttpPort, out var adminPort))
        {
            after.Admin.HttpPort = adminPort;
        }

        if (_values.TryGetValue(SetupWizardValueKeys.RetentionDays, out var retentionDays))
        {
            after.Retention.Days = retentionDays;
        }

        return after;
    }

    private static string BuildAuditDetail(ConfigurationChangePlan plan)
    {
        if (!plan.HasChanges)
        {
            return "初期セットアップウィザードによる設定保存（変更されたキーなし——既存内容と同値）";
        }

        return "初期セットアップウィザードによる設定保存: " +
               string.Join(", ", plan.ChangedKeys) +
               $" / 反映方式={plan.RequiredEffect}";
    }

    private static ConfigurationApplyEffect ToApplyEffect(ConfigurationReloadEffect effect) => effect switch
    {
        ConfigurationReloadEffect.Immediate => ConfigurationApplyEffect.Immediate,
        ConfigurationReloadEffect.ListenerReconfiguration => ConfigurationApplyEffect.ListenerReconfiguration,
        _ => ConfigurationApplyEffect.RestartRequired,
    };
}
