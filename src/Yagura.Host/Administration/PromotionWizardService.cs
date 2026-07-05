using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Administration;

/// <summary>
/// 本番昇格（SQLite → SQL Server）ウィザードの実体（<see cref="IPromotionWizardService"/>。
/// M8-4。Issue #71）。
/// </summary>
/// <remarks>
/// <para>
/// <b>資格情報の統治（configuration.md §5）</b>: 接続文字列は本シングルトンのメモリ
/// （<see cref="_connectionString"/>）にのみ保持する。サーバ側セッション状態のうち
/// 「進行状態」（処分の選択・検証済みフラグ等）と「資格情報」を分離し、無操作タイムアウト
/// （CF-3 仮値 15 分 = <see cref="WizardSessionDefaults.InactivityTimeout"/>）で資格情報だけを
/// 破棄する。破棄時は検証済み状態・実行トークンも無効化する——検証は「現に保持している
/// 接続文字列」に対してのみ有効であり、破棄後の実行を許すと未検証の切替になるため。
/// ディスク・ログ・監査記録には書かない（監査は「使用した」事実と成否のみ）。
/// </para>
/// <para>
/// <b>M8-4 骨格の既知の制約（後続 Issue へ申し送り）</b>: 切替実行は検証済みの接続文字列を
/// <c>Storage:SqlServer:ConnectionString</c> として設定ファイルへ保存する。configuration.md §2 の
/// DPAPI 暗号化は M5-3 時点で検出の枠組みのみ（<see cref="SqlServerConnectionStringCredentialGuard"/>）
/// が存在し、暗号化の実装は後続 Issue のため、<b>保存時点では平文で書かれる</b>（手編集と同じ
/// 現状の水準。暗号化実装後にこの経路も自動的に暗号化表現になる）。切替本番の実行時手順
/// （database.md §6.1 ①〜④の無瞬断切替・旧 DB ファイルの実処分）も同様に後続 Issue。
/// </para>
/// </remarks>
public sealed class PromotionWizardService : IPromotionWizardService
{
    private readonly object _gate = new();
    private readonly string _dataRoot;
    private readonly ISqlServerConnectionValidator _connectionValidator;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;

    // ---- 進行状態（サーバ側セッション。タイムアウトでも破棄しない——configuration.md §5） ----
    private OldDatabaseDisposal? _disposal;
    private bool _executed;
    private string? _executedToken;
    private PromotionApplyResult? _executedResult;

    // ---- 資格情報（メモリのみ。タイムアウトで破棄——configuration.md §5） ----
    private string? _connectionString;
    private bool _connectionValidated;
    private string? _executeToken;
    private bool _credentialReentryRequired;
    private DateTimeOffset _lastActivityAt;

    public PromotionWizardService(
        string dataRoot,
        ISqlServerConnectionValidator connectionValidator,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(connectionValidator);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _dataRoot = dataRoot;
        _connectionValidator = connectionValidator;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastActivityAt = _timeProvider.GetUtcNow();
    }

    /// <inheritdoc/>
    public Task<PromotionWizardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Touch();
            return Task.FromResult(BuildSnapshot());
        }
    }

    /// <inheritdoc/>
    public Task<PromotionWizardSnapshot> SetConnectionStringAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new WizardValidationException("接続文字列を入力してください。");
        }

        lock (_gate)
        {
            Touch();

            _connectionString = connectionString;
            _credentialReentryRequired = false;

            // 検証済み状態は新しい接続文字列に対して無効（検証は保持中の値に対してのみ有効）。
            _connectionValidated = false;
            _executeToken = null;

            return Task.FromResult(BuildSnapshot());
        }
    }

    /// <inheritdoc/>
    public async Task<PromotionValidationResult> ValidateConnectionAsync(
        string? operatorAddress = null,
        CancellationToken cancellationToken = default)
    {
        string connectionString;

        lock (_gate)
        {
            Touch();

            if (_connectionString is null)
            {
                return new PromotionValidationResult(
                    false,
                    "接続文字列が未入力か、破棄されています。入力し直してください。",
                    CredentialRequired: true);
            }

            connectionString = _connectionString;
        }

        // 接続試行はロックの外（ネットワーク I/O を直列化のロックで抱えない）。
        var validation = await _connectionValidator.ValidateAsync(connectionString, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            // 検証中にタイムアウト・差し替えが起きていたら結果を採用しない（安全側）。
            if (ReferenceEquals(_connectionString, connectionString) && validation.Success)
            {
                _connectionValidated = true;
                _executeToken = Guid.NewGuid().ToString("N");
            }
        }

        // 監査記録（2000 番台 ID 2002）: 管理者資格情報を「使用した」事実と成否のみを残す
        // （configuration.md §5——資格情報そのもの・検証メッセージの詳細は記録しない）。
        await _auditRecorder.RecordAsync(
            new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.PromotionConnectionValidated,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail: $"SQL Server 接続検証: {(validation.Success ? "成功" : "失敗")}（管理者資格情報を使用）"),
            CancellationToken.None).ConfigureAwait(false);

        return new PromotionValidationResult(validation.Success, validation.Message);
    }

    /// <inheritdoc/>
    public Task<PromotionWizardSnapshot> ChooseOldDatabaseDisposalAsync(
        OldDatabaseDisposal disposal,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Touch();
            _disposal = disposal;
            return Task.FromResult(BuildSnapshot());
        }
    }

    /// <inheritdoc/>
    public async Task<PromotionApplyResult> ExecuteAsync(
        string idempotencyToken,
        string? operatorAddress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyToken);

        PromotionApplyResult result;
        AuditEvent auditEvent;

        lock (_gate)
        {
            Touch();

            // 一回性の保証（configuration.md §7）。
            if (_executedToken is not null && string.Equals(idempotencyToken, _executedToken, StringComparison.Ordinal))
            {
                return _executedResult! with { Outcome = WizardApplyOutcome.AlreadyApplied };
            }

            if (_executeToken is null ||
                !string.Equals(idempotencyToken, _executeToken, StringComparison.Ordinal) ||
                !_connectionValidated ||
                _connectionString is null)
            {
                return new PromotionApplyResult(
                    WizardApplyOutcome.InvalidToken,
                    ConfigurationApplyEffect.Immediate,
                    "実行トークンが無効か、接続検証が完了していません。");
            }

            if (_disposal is null)
            {
                return new PromotionApplyResult(
                    WizardApplyOutcome.InvalidToken,
                    ConfigurationApplyEffect.Immediate,
                    "旧・組み込みデータベースの扱い（退避 / 削除）を先に選択してください。");
            }

            // 「読み込み → 変更 → 検証 → 保存」（configuration.md §3）。切替は短時間の確定操作の
            // ため、読み込みと保存を同一ロック内で行う（外部変更はバージョントークンが検出する）。
            var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
            var after = YaguraConfigurationOptionsCloner.Clone(snapshot.Options);
            after.Storage ??= new YaguraConfigurationOptions.StorageOptions();
            after.Storage.Provider = "sqlserver";
            after.Storage.SqlServer ??= new YaguraConfigurationOptions.SqlServerOptions();
            after.Storage.SqlServer.ConnectionString = _connectionString;

            try
            {
                YaguraConfigurationWriter.Save(_dataRoot, after, snapshot.VersionToken);
            }
            catch (ConfigurationConflictException)
            {
                return new PromotionApplyResult(
                    WizardApplyOutcome.Conflict,
                    ConfigurationApplyEffect.Immediate,
                    "設定ファイルが同時に変更されたため、切替を中止しました。やり直してください。");
            }

            result = new PromotionApplyResult(
                WizardApplyOutcome.Applied,
                // M8-4 骨格の実効: サービス再起動（configuration.md §8 の Storage:Provider 行。
                // database.md §6.1 の無瞬断切替手順の実装後に見直す）。
                ConfigurationApplyEffect.RestartRequired,
                "保存先の切り替えを設定に保存しました。旧データベースの処分（" +
                (_disposal == OldDatabaseDisposal.Evacuate ? "退避" : "削除") +
                "）は切替手順の実装（後続）で行われるまで未完了として扱われます。");

            _executed = true;
            _executedToken = idempotencyToken;
            _executedResult = result;

            // 完了に伴い資格情報を破棄する（configuration.md §5「終了時に破棄」。設定ファイルへの
            // 保存が完了した後はメモリ保持を続ける理由がない）。
            _connectionString = null;
            _connectionValidated = false;
            _executeToken = null;

            // 監査記録（2000 番台 ID 2003）: 接続文字列は記録しない（成立の事実と選択のみ）。
            auditEvent = new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.PromotionExecuted,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail: "Storage:Provider を sqlite から sqlserver へ切替（設定保存。反映はサービス再起動）。" +
                        $"旧 DB 処分の選択={(_disposal == OldDatabaseDisposal.Evacuate ? "退避" : "削除")}");
        }

        await _auditRecorder.RecordAsync(auditEvent, CancellationToken.None).ConfigureAwait(false);

        return result;
    }

    private PromotionWizardSnapshot BuildSnapshot() => new(
        HasConnectionString: _connectionString is not null,
        ConnectionValidated: _connectionValidated,
        Disposal: _disposal,
        ExecuteIdempotencyToken: _executeToken,
        Executed: _executed,
        CredentialReentryRequired: _credentialReentryRequired);

    /// <summary>
    /// 無操作タイムアウト（CF-3 仮値 15 分）の適用と最終活動時刻の更新。資格情報のみ破棄し、
    /// 進行状態（処分の選択・実行済み状態）は保持する（configuration.md §5）。
    /// </summary>
    private void Touch()
    {
        var now = _timeProvider.GetUtcNow();

        if (_connectionString is not null &&
            now - _lastActivityAt >= WizardSessionDefaults.InactivityTimeout)
        {
            _connectionString = null;
            _connectionValidated = false;
            _executeToken = null;
            _credentialReentryRequired = true;
        }

        _lastActivityAt = now;
    }
}
