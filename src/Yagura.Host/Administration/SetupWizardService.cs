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

            // 入力ステップ（Reception/ViewerAccess/Retention）の再確定で値が変わり得るため、
            // 確認ステップが確定済みならその確定と発行済みトークン・読み込み済みスナップショットを
            // 破棄する（Issue #248）。古い確認内容・トークンのまま適用される事故を防ぐ——
            // GoBackAsync の Review 破棄と同じ意味論（configuration.md §7 の一回性: トークンは
            // 「確認した内容」と 1 対 1 に対応し、内容が変わったら確認をやり直す）。
            if (step != SetupWizardStep.Review && _confirmedSteps.Contains(SetupWizardStep.Review))
            {
                _confirmedSteps.Remove(SetupWizardStep.Review);
                _reviewSnapshot = null;
                _applyToken = null;
            }

            if (!_confirmedSteps.Contains(step))
            {
                _confirmedSteps.Add(step);
            }

            return Task.FromResult(BuildSnapshot());
        }
    }

    /// <inheritdoc/>
    public Task<SetupWizardSnapshot> GoBackAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_appliedResult is not null)
            {
                throw new WizardValidationException("設定は既に保存済みです。変更はやり直しではなく設定変更として行ってください。");
            }

            if (_confirmedSteps.Count == 0)
            {
                throw new WizardValidationException("最初のステップのため、これ以上前へは戻れません。");
            }

            // 最後に確定したステップの確定を取り消し、そのステップへ戻る（NextStep がそこへ戻る）。
            // 入力値（_values）は保持する——戻り先フォームに再表示して再編集できるようにするため。
            var lastConfirmed = _confirmedSteps[^1];
            _confirmedSteps.RemoveAt(_confirmedSteps.Count - 1);

            // 確認ステップを取り消す場合は、発行済みの適用用トークンと読み込み済みスナップショットも破棄する
            // （前ステップの値が変わり得るため、確認〔読み込み → トークン発行〕を再度やり直す）。
            if (lastConfirmed == SetupWizardStep.Review)
            {
                _reviewSnapshot = null;
                _applyToken = null;
            }

            return Task.FromResult(BuildSnapshot());
        }
    }

    /// <inheritdoc/>
    public Task<SetupWizardSnapshot> BeginReconfigurationAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // 現在の設定ファイルを読み込む（configuration.md §3「読み込み → 変更 → 検証 → 保存」の
            // 「読み込み」に相当するが、ここでは入力値の種にするだけ——楽観競合検出の基準になる
            // 読み込みは、あらためて確認ステップの確定時に行う）。
            var current = YaguraConfigurationWriter.Read(_dataRoot);

            // 再編集する対象がない（未適用・ファイル無し）場合は通常のウィザード進行で足りる。
            if (_appliedResult is null && current.VersionToken.Equals(ConfigurationVersionToken.FileAbsent))
            {
                throw new WizardValidationException(
                    "設定はまだ適用されていないため、最初からやり直す必要はありません。そのままウィザードを進めてください。");
            }

            // 現在の設定値をウィザードの入力値へ写像する。値が無いキーは初期セットアップの
            // UI 既定値と同じ既定へフォールバックする（フォームを空にしない——再編集の起点は
            // 常に「いまの実効に近い値」にする）。
            var options = current.Options;
            _values.Clear();
            _values[SetupWizardValueKeys.UdpPort] = options.Ingestion?.Udp?.Port ?? "514";
            _values[SetupWizardValueKeys.TcpPort] = options.Ingestion?.Tcp?.Port ?? "514";
            _values[SetupWizardValueKeys.ViewerHttpPort] = options.Viewer?.HttpPort ?? "8514";
            _values[SetupWizardValueKeys.ViewerPublicAccess] = options.Viewer?.PublicAccess ?? "Lan";
            _values[SetupWizardValueKeys.AdminHttpPort] = options.Admin?.HttpPort ?? "8515";
            _values[SetupWizardValueKeys.RetentionDays] = options.Retention?.Days ?? "30";

            // 3 つの入力ステップを確定済み扱いにする（ステッパーで全ステップへ即移動できる）。
            // 確認ステップは未確定のまま——適用にはあらためて確認の確定（新トークンの発行）を要する。
            _confirmedSteps.Clear();
            _confirmedSteps.Add(SetupWizardStep.Reception);
            _confirmedSteps.Add(SetupWizardStep.ViewerAccess);
            _confirmedSteps.Add(SetupWizardStep.Retention);

            // 適用ロックを解除する（configuration.md §7 の一回性は「トークンごと」の保証であり、
            // 過去の適用済みトークンを破棄しても、新しい適用には新しいトークンが必要なため
            // 二重適用の抑止は崩れない）。再編集の開始は状態を保存しないため監査記録は行わない
            // （設定の変更は適用時の 2001 が引き続き記録する）。
            _reviewSnapshot = null;
            _applyToken = null;
            _appliedToken = null;
            _appliedResult = null;

            return Task.FromResult(BuildSnapshot());
        }
    }

    /// <inheritdoc/>
    public async Task<SetupWizardApplyResult> ApplyAsync(
        string idempotencyToken,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
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
                Detail: BuildAuditDetail(plan),
                AuthenticationScheme: operatorScheme,
                AuthenticatedPrincipal: operatorPrincipal);
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
