using Microsoft.Data.SqlClient;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Administration;

/// <summary>
/// 本番昇格（SQLite → SQL Server）ウィザードの実体（<see cref="IPromotionWizardService"/>。
/// M8-4 骨格 = Issue #71。接続組み立て・失敗分類・修復 SQL 提示の UX 完成 = PR #102）。
/// </summary>
/// <remarks>
/// <para>
/// <b>資格情報の統治（configuration.md §5）</b>: 破棄の対象となる資格情報は<b>パスワードのみ</b>
/// （<see cref="_password"/>。本シングルトンのメモリにのみ保持）。接続の項目・パスワードを
/// 含まない直接入力の接続文字列は秘密ではなく、進行状態として無操作タイムアウト
/// （CF-3 仮値 15 分 = <see cref="WizardSessionDefaults.InactivityTimeout"/>）を越えて保持する。
/// タイムアウトはパスワードとともに検証済み状態・実行トークンを無効化する——検証は「現に
/// 保持している入力」に対してのみ有効であり、破棄後の実行を許すと未検証の切替になるため。
/// パスワードはディスク・ログ・監査記録には書かない（監査は「使用した」事実と成否・分類のみ）。
/// </para>
/// <para>
/// <b>パスワードの平文経路の遮断（database.md §6.1）</b>: パスワードは入力方式によらず専用の
/// 引数でのみ受け取る。直接入力の接続文字列は <see cref="SqlConnectionStringBuilder"/> で
/// 正規化パースし、パスワード系キー（<c>Pwd</c> 別名・大文字小文字ゆれを含む——builder が
/// <c>Password</c> へ正規化する）の記載を拒否する。snapshot にパスワードそのものは含めない。
/// </para>
/// <para>
/// <b>資格情報の保存形式（configuration.md §2。ADR-0004 決定 5）</b>: 切替実行は検証済みの
/// 接続文字列を <see cref="DpapiConnectionStringProtector"/> で
/// <b>常に DPAPI 暗号化表現（<c>dpapi:&lt;Base64&gt;</c>）へ暗号化してから</b>
/// <c>Storage:SqlServer:ConnectionString</c> として設定ファイルへ保存する——平文が設定ファイルへ
/// 書かれる経路をウィザードには残さない。暗号化自体が失敗した場合は例外を伝播させ、
/// <b>平文での保存へはフォールバックしない</b>。
/// </para>
/// <para>
/// <b>M8-4 骨格の既知の制約（後続 Issue へ申し送り）</b>: 切替本番の実行時手順
/// （database.md §6.1 ①〜④の無瞬断切替・旧 DB ファイルの実処分・退避先の存在/書込可否の
/// 事前検証）は後続 Issue。
/// </para>
/// </remarks>
public sealed class PromotionWizardService : IPromotionWizardService
{
    private readonly object _gate = new();
    private readonly string _dataRoot;
    private readonly ISqlServerConnectionValidator _connectionValidator;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;
    private readonly string _serviceAccountName;

    // ---- 進行状態（サーバ側セッション。タイムアウトでも破棄しない——configuration.md §5） ----
    private PromotionConnectionInputMode _inputMode = PromotionConnectionInputMode.Form;
    private PromotionConnectionForm? _form;
    private string? _rawConnectionString;
    private OldDatabaseDisposal? _disposal;
    private string? _evacuationDirectory;
    private bool _executed;
    private string? _executedToken;
    private PromotionApplyResult? _executedResult;

    // ---- 資格情報と検証状態（パスワードのみ資格情報。タイムアウトで破棄——configuration.md §5） ----
    private string? _password;
    private bool _connectionValidated;
    private string? _executeToken;
    private bool _credentialReentryRequired;
    private DateTimeOffset _lastActivityAt;

    // 検証の I/O（ロック外）の間に入力が差し替え・破棄されていないかを検出する版数
    // （検証は「現に保持している入力」に対してのみ有効、の実装形）。
    private int _stateVersion;

    public PromotionWizardService(
        string dataRoot,
        ISqlServerConnectionValidator connectionValidator,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null,
        string? serviceAccountName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(connectionValidator);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _dataRoot = dataRoot;
        _connectionValidator = connectionValidator;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serviceAccountName = serviceAccountName ?? ResolveCurrentAccountName();
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
    public Task<PromotionWizardSnapshot> SetConnectionFormAsync(
        PromotionConnectionForm form,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.ServerName))
        {
            throw new WizardValidationException("サーバ名を入力してください。");
        }

        if (string.IsNullOrWhiteSpace(form.DatabaseName))
        {
            throw new WizardValidationException("データベース名を入力してください。");
        }

        if (form.AuthenticationMode == PromotionAuthenticationMode.SqlServer &&
            string.IsNullOrWhiteSpace(form.UserName))
        {
            throw new WizardValidationException("SQL Server 認証ではユーザー名を入力してください。");
        }

        lock (_gate)
        {
            Touch();

            _inputMode = PromotionConnectionInputMode.Form;
            _form = form;

            // パスワードは Windows 統合認証では存在しない（受け取っても破棄する）。SQL Server
            // 認証では常に置き換え——null/空白は「未入力」であり、保持中の値も消す（画面側の
            // 入力欄が唯一の入力経路であることを保つ）。
            _password = form.AuthenticationMode == PromotionAuthenticationMode.SqlServer &&
                        !string.IsNullOrWhiteSpace(password)
                ? password
                : null;
            _credentialReentryRequired = false;

            InvalidateValidationState();

            return Task.FromResult(BuildSnapshot());
        }
    }

    /// <inheritdoc/>
    public Task<PromotionWizardSnapshot> SetRawConnectionStringAsync(
        string connectionString,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new WizardValidationException("接続文字列を入力してください。");
        }

        // 正規化パース（Pwd 別名・大文字小文字・空白ゆれは builder が Password へ正規化する）。
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            throw new WizardValidationException($"接続文字列を解釈できません: {ex.Message}");
        }

        // キーの存在は空値でも検出する（ShouldSerialize は正規化後のキー名で判定する）。
        if (builder.ShouldSerialize("Password") || !string.IsNullOrEmpty(builder.Password))
        {
            throw new WizardValidationException(
                "パスワードは接続文字列に書かず、パスワード欄で入力してください" +
                "（Password / Pwd キーは受け付けません——画面に平文が残る経路を作らないため）。");
        }

        lock (_gate)
        {
            Touch();

            _inputMode = PromotionConnectionInputMode.Raw;
            _rawConnectionString = connectionString;
            _password = string.IsNullOrWhiteSpace(password) ? null : password;
            _credentialReentryRequired = false;

            InvalidateValidationState();

            return Task.FromResult(BuildSnapshot());
        }
    }

    /// <inheritdoc/>
    public async Task<PromotionValidationResult> ValidateConnectionAsync(
        string? operatorAddress = null,
        CancellationToken cancellationToken = default)
    {
        string connectionString;
        int versionAtStart;

        lock (_gate)
        {
            Touch();

            var built = BuildConnectionStringCore();
            if (built.ConnectionString is null)
            {
                return new PromotionValidationResult(
                    false,
                    built.UnavailableMessage!,
                    CredentialRequired: true);
            }

            connectionString = built.ConnectionString;
            versionAtStart = _stateVersion;
        }

        // 接続試行はロックの外（ネットワーク I/O を直列化のロックで抱えない）。
        var validation = await _connectionValidator.ValidateAsync(connectionString, cancellationToken).ConfigureAwait(false);

        string? remediationSql = null;

        lock (_gate)
        {
            // 検証中にタイムアウト・差し替えが起きていたら結果を採用しない（安全側）。
            if (_stateVersion == versionAtStart && validation.Success)
            {
                _connectionValidated = true;
                _executeToken = Guid.NewGuid().ToString("N");
            }

            if (!validation.Success)
            {
                remediationSql = BuildRemediationSqlCore(validation.FailureKind);
            }
        }

        // 監査記録（2000 番台 ID 2002）: 管理者資格情報を「使用した」事実・成否・失敗の分類・
        // 証明書信頼の選択値のみを残す（configuration.md §5——資格情報そのもの・修復 SQL の
        // 原文・検証メッセージの詳細は記録しない。database.md §6.1）。
        await _auditRecorder.RecordAsync(
            new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.PromotionConnectionValidated,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail: BuildValidationAuditDetail(validation)),
            CancellationToken.None).ConfigureAwait(false);

        return new PromotionValidationResult(
            validation.Success,
            validation.Message,
            CredentialRequired: false,
            FailureKind: validation.FailureKind,
            RemediationSql: remediationSql);
    }

    /// <inheritdoc/>
    public Task<PromotionWizardSnapshot> ChooseOldDatabaseDisposalAsync(
        OldDatabaseDisposal disposal,
        string? evacuationDirectory = null,
        CancellationToken cancellationToken = default)
    {
        string? normalizedDirectory = null;

        if (disposal == OldDatabaseDisposal.Evacuate)
        {
            normalizedDirectory = evacuationDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedDirectory))
            {
                throw new WizardValidationException("退避先のフォルダを入力してください。");
            }

            // 形式（絶対パス）までを本改善の検証とする。存在・書込可否の事前検証は旧 DB
            // ファイルの実処分の実装（後続 Issue）の要件（database.md §6.1）。
            if (!Path.IsPathFullyQualified(normalizedDirectory))
            {
                throw new WizardValidationException(
                    "退避先はドライブ名から始まる絶対パスで入力してください（例: D:\\Backup\\Yagura）。");
            }
        }

        lock (_gate)
        {
            Touch();
            _disposal = disposal;
            _evacuationDirectory = disposal == OldDatabaseDisposal.Evacuate ? normalizedDirectory : null;
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
                !_connectionValidated)
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

            // 検証済みの間は入力が差し替えられていない（差し替えは検証状態を無効化する）ため、
            // ここでの再組み立ては検証時と同じ接続文字列になる。
            var built = BuildConnectionStringCore();
            if (built.ConnectionString is null)
            {
                return new PromotionApplyResult(
                    WizardApplyOutcome.InvalidToken,
                    ConfigurationApplyEffect.Immediate,
                    "接続の入力が破棄されています。接続の検証からやり直してください。");
            }

            // 「読み込み → 変更 → 検証 → 保存」（configuration.md §3）。切替は短時間の確定操作の
            // ため、読み込みと保存を同一ロック内で行う（外部変更はバージョントークンが検出する）。
            var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
            var after = YaguraConfigurationOptionsCloner.Clone(snapshot.Options);
            after.Storage ??= new YaguraConfigurationOptions.StorageOptions();
            after.Storage.Provider = "sqlserver";
            after.Storage.SqlServer ??= new YaguraConfigurationOptions.SqlServerOptions();

            // 常に DPAPI 暗号化表現で保存する（configuration.md §2。ADR-0004 決定 5。
            // 暗号化失敗時は例外を伝播——平文保存へはフォールバックしない。クラス remarks 参照）。
            after.Storage.SqlServer.ConnectionString = DpapiConnectionStringProtector.Protect(built.ConnectionString);

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
            _password = null;
            InvalidateValidationState();

            // 監査記録（2000 番台 ID 2003）: 接続文字列・パスワードは記録しない（成立の事実と
            // 選択・退避先・証明書信頼の選択値のみ——database.md §6.1）。
            var disposalDetail = _disposal == OldDatabaseDisposal.Evacuate
                ? $"退避（退避先={_evacuationDirectory}）"
                : "削除";
            auditEvent = new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.PromotionExecuted,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail: "Storage:Provider を sqlite から sqlserver へ切替（設定保存。反映はサービス再起動）。" +
                        $"旧 DB 処分の選択={disposalDetail}、" +
                        $"サーバ証明書の信頼={(built.TrustServerCertificate ? "有効" : "無効")}");
        }

        await _auditRecorder.RecordAsync(auditEvent, CancellationToken.None).ConfigureAwait(false);

        return result;
    }

    private PromotionWizardSnapshot BuildSnapshot() => new(
        InputMode: _inputMode,
        Form: _form,
        RawConnectionString: _rawConnectionString,
        ServiceAccountName: _serviceAccountName,
        HasPassword: _password is not null,
        ConnectionValidated: _connectionValidated,
        Disposal: _disposal,
        EvacuationDirectory: _evacuationDirectory,
        ExecuteIdempotencyToken: _executeToken,
        Executed: _executed,
        CredentialReentryRequired: _credentialReentryRequired);

    /// <summary>
    /// 保持中の入力から接続文字列を組み立てる（呼び出し側が <see cref="_gate"/> を保持する）。
    /// 組み立てられない場合は理由（利用者向け文言）を返す。
    /// </summary>
    private (string? ConnectionString, bool TrustServerCertificate, string? UnavailableMessage) BuildConnectionStringCore()
    {
        if (_inputMode == PromotionConnectionInputMode.Form)
        {
            if (_form is null)
            {
                return (null, false, "接続の入力がありません。サーバ名から入力してください。");
            }

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = _form.ServerName,
                InitialCatalog = _form.DatabaseName,
                TrustServerCertificate = _form.TrustServerCertificate,
            };

            if (_form.AuthenticationMode == PromotionAuthenticationMode.WindowsIntegrated)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                if (_password is null)
                {
                    return (null, _form.TrustServerCertificate,
                        "パスワードが未入力か、破棄されています。入力し直してください。");
                }

                builder.UserID = _form.UserName!;
                builder.Password = _password;
            }

            return (builder.ConnectionString, _form.TrustServerCertificate, null);
        }

        if (_rawConnectionString is null)
        {
            return (null, false, "接続文字列が未入力か、破棄されています。入力し直してください。");
        }

        // 設定時に解釈可能なことは検証済み（SetRawConnectionStringAsync）。
        var raw = new SqlConnectionStringBuilder(_rawConnectionString);
        if (_password is not null)
        {
            raw.Password = _password;
        }

        return (raw.ConnectionString, raw.TrustServerCertificate, null);
    }

    /// <summary>
    /// 失敗分類に応じた修復 SQL を生成する（呼び出し側が <see cref="_gate"/> を保持する。
    /// database.md §5.2・§6.1——提示のみでありサーバは実行しない。ログイン名・DB 名が入力から
    /// 特定できない場合は生成しない = 断定提示しない安全側）。
    /// </summary>
    private string? BuildRemediationSqlCore(PromotionConnectionFailureKind failureKind)
    {
        if (failureKind is not (PromotionConnectionFailureKind.LoginFailed or PromotionConnectionFailureKind.DatabaseNotFound))
        {
            return null;
        }

        PromotionAuthenticationMode authenticationMode;
        string? loginName;
        string? databaseName;

        if (_inputMode == PromotionConnectionInputMode.Form)
        {
            if (_form is null)
            {
                return null;
            }

            authenticationMode = _form.AuthenticationMode;
            loginName = authenticationMode == PromotionAuthenticationMode.WindowsIntegrated
                ? _serviceAccountName
                : _form.UserName;
            databaseName = _form.DatabaseName;
        }
        else
        {
            if (_rawConnectionString is null)
            {
                return null;
            }

            var raw = new SqlConnectionStringBuilder(_rawConnectionString);
            authenticationMode = raw.IntegratedSecurity
                ? PromotionAuthenticationMode.WindowsIntegrated
                : PromotionAuthenticationMode.SqlServer;
            loginName = raw.IntegratedSecurity ? _serviceAccountName : raw.UserID;
            databaseName = raw.InitialCatalog;
        }

        if (string.IsNullOrWhiteSpace(loginName) || string.IsNullOrWhiteSpace(databaseName))
        {
            return null;
        }

        return failureKind == PromotionConnectionFailureKind.LoginFailed
            ? PromotionRemediationSql.ForLoginFailed(authenticationMode, loginName, databaseName)
            : PromotionRemediationSql.ForDatabaseNotFound(loginName, databaseName);
    }

    /// <summary>検証の監査記録（2002）の Detail（呼び出し側のロックは不要——不変の入力のみ使う）。</summary>
    private string BuildValidationAuditDetail(SqlServerConnectionValidationResult validation)
    {
        string authentication;
        bool trustServerCertificate;

        lock (_gate)
        {
            if (_inputMode == PromotionConnectionInputMode.Form && _form is not null)
            {
                authentication = _form.AuthenticationMode == PromotionAuthenticationMode.WindowsIntegrated
                    ? "Windows 統合認証"
                    : "SQL Server 認証";
                trustServerCertificate = _form.TrustServerCertificate;
            }
            else
            {
                authentication = "接続文字列の直接入力";
                trustServerCertificate = _rawConnectionString is not null &&
                    new SqlConnectionStringBuilder(_rawConnectionString).TrustServerCertificate;
            }
        }

        var detail = $"SQL Server 接続検証: {(validation.Success ? "成功" : "失敗")}" +
                     $"（管理者資格情報を使用。認証方式={authentication}、" +
                     $"サーバ証明書の信頼={(trustServerCertificate ? "有効" : "無効")}";

        if (!validation.Success)
        {
            detail += $"、原因分類={FormatFailureKind(validation.FailureKind)}";
        }

        return detail + "）";
    }

    private static string FormatFailureKind(PromotionConnectionFailureKind kind) => kind switch
    {
        PromotionConnectionFailureKind.CertificateNotTrusted => "サーバ証明書不信頼",
        PromotionConnectionFailureKind.ServerUnreachable => "サーバ到達不能",
        PromotionConnectionFailureKind.LoginFailed => "ログイン失敗",
        PromotionConnectionFailureKind.DatabaseNotFound => "データベース不在",
        _ => "分類不能",
    };

    /// <summary>
    /// サービスの実行アカウント名（Windows 統合認証で SQL Server 側に見える名前——画面表示と
    /// 修復 SQL の実値に使う。database.md §6.1）。本製品は Windows 専用（ADR-0001）であり、
    /// 非 Windows 分岐は CA1416（プラットフォーム互換性解析）を満たすためのガード。
    /// </summary>
    private static string ResolveCurrentAccountName() =>
        OperatingSystem.IsWindows()
            ? System.Security.Principal.WindowsIdentity.GetCurrent().Name
            : Environment.UserName;

    /// <summary>
    /// 検証済み状態・実行トークンの無効化（入力の変更・タイムアウト・完了時。検証は「現に
    /// 保持している入力」に対してのみ有効）。
    /// </summary>
    private void InvalidateValidationState()
    {
        _connectionValidated = false;
        _executeToken = null;
        _stateVersion++;
    }

    /// <summary>
    /// 無操作タイムアウト（CF-3 仮値 15 分）の適用と最終活動時刻の更新。資格情報（パスワード）
    /// と検証済み状態・実行トークンを破棄し、進行状態（接続の項目・処分の選択・実行済み状態）は
    /// 保持する（configuration.md §5 の粒度）。
    /// </summary>
    private void Touch()
    {
        var now = _timeProvider.GetUtcNow();

        if (now - _lastActivityAt >= WizardSessionDefaults.InactivityTimeout &&
            (_password is not null || _connectionValidated || _executeToken is not null))
        {
            // 再入力の明示（configuration.md §5）はパスワードを失ったときだけ——Windows 統合
            // 認証等でパスワードがない場合、再開に必要なのは再検証のみで再入力ではない。
            if (_password is not null)
            {
                _password = null;
                _credentialReentryRequired = true;
            }

            InvalidateValidationState();
        }

        _lastActivityAt = now;
    }
}
