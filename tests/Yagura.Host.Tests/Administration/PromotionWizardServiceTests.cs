using System.Text.Json;
using Microsoft.Data.SqlClient;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Administration;

/// <summary>
/// <see cref="PromotionWizardService"/> の単体テスト（M8-4 骨格 = Issue #71。接続組み立て・
/// 失敗分類・修復 SQL 提示・退避先 = PR #102。database.md §6.1・configuration.md §5）。
/// </summary>
/// <remarks>
/// 実際の SQL Server は使わない——接続検証は <see cref="ISqlServerConnectionValidator"/> の
/// テスト実装で差し替え、経路（組み立て → 検証 → 選択 → 実行・監査・冪等性・資格情報統治）を
/// 検証する（Issue #71「実際の SQL Server がない開発機でもテストできる形」の実装形）。
/// </remarks>
public sealed class PromotionWizardServiceTests : IDisposable
{
    private const string ServiceAccount = @"NT SERVICE\Yagura";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-promotion-test-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _audit = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero));

    public PromotionWizardServiceTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // ベストエフォート。
            }
        }
    }

    // ---- 接続の組み立て（database.md §6.1「接続文字列はサーバ側で組み立てる」） ----

    [Fact]
    public async Task SetConnectionForm_WindowsAuth_BuildsIntegratedSecurityConnectionString()
    {
        var validator = new FakeValidator(success: true);
        var service = CreateService(validator);

        await service.SetConnectionFormAsync(WindowsForm());
        var result = await service.ValidateConnectionAsync();

        Assert.True(result.Success);
        var built = new SqlConnectionStringBuilder(validator.LastConnectionString);
        Assert.Equal(@"SV01\SQLEXPRESS", built.DataSource);
        Assert.Equal("Yagura", built.InitialCatalog);
        Assert.True(built.IntegratedSecurity);
        Assert.False(built.TrustServerCertificate);
    }

    [Fact]
    public async Task SetConnectionForm_SqlAuth_BuildsCredentialConnectionString_ButKeepsPasswordOutOfSnapshot()
    {
        var validator = new FakeValidator(success: true);
        var service = CreateService(validator);

        var snapshot = await service.SetConnectionFormAsync(SqlForm(trustServerCertificate: true), password: "secret!");
        var result = await service.ValidateConnectionAsync();

        Assert.True(result.Success);
        var built = new SqlConnectionStringBuilder(validator.LastConnectionString);
        Assert.Equal("sa", built.UserID);
        Assert.Equal("secret!", built.Password);
        Assert.True(built.TrustServerCertificate);

        // snapshot にパスワードそのものは現れない（configuration.md §5——保持の有無のみ）。
        Assert.True(snapshot.HasPassword);
        Assert.Equal("sa", snapshot.Form!.UserName);
        Assert.DoesNotContain("secret!", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetConnectionForm_MissingRequiredFields_IsRejected()
    {
        var service = CreateService(new FakeValidator(success: true));

        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.SetConnectionFormAsync(WindowsForm() with { ServerName = " " }));
        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.SetConnectionFormAsync(WindowsForm() with { DatabaseName = "" }));
        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.SetConnectionFormAsync(SqlForm() with { UserName = null }));
    }

    [Fact]
    public async Task ValidateConnection_SqlAuthWithoutPassword_RequiresCredential()
    {
        var service = CreateService(new FakeValidator(success: true));
        await service.SetConnectionFormAsync(SqlForm());

        var result = await service.ValidateConnectionAsync();

        Assert.False(result.Success);
        Assert.True(result.CredentialRequired);
        Assert.Empty(_audit.RecordedEvents);
    }

    // ---- 直接入力（パスワード系キーの構造的拒否——database.md §6.1） ----

    [Theory]
    [InlineData("Server=db;User Id=sa;Password=secret!")]
    [InlineData("Server=db;User Id=sa;pwd=secret!")]
    [InlineData("Server=db;User Id=sa;PWD = secret!")]
    public async Task SetRawConnectionString_WithPasswordKey_IsRejected(string connectionString)
    {
        var service = CreateService(new FakeValidator(success: true));

        // Pwd 別名・大文字小文字・空白ゆれも正規化パースで拒否する（PR #102 ペルソナレビュー
        // 田中/リサ/クリスの指摘の固定化）。値が空の "Password=" は builder の正規化自体が
        // キーごと落とすため検出できないが、空値は秘密を含まず拒否の目的（平文の混入防止）の
        // 対象外——実挙動の確認済み（このテスト自身が実機確認を兼ねる）。
        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.SetRawConnectionStringAsync(connectionString));

        var snapshot = await service.GetSnapshotAsync();
        Assert.Null(snapshot.RawConnectionString);
    }

    [Fact]
    public async Task SetRawConnectionString_Unparseable_IsRejected()
    {
        var service = CreateService(new FakeValidator(success: true));

        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.SetRawConnectionStringAsync("これは接続文字列ではない"));
    }

    [Fact]
    public async Task SetRawConnectionString_WithSeparatePassword_MergesIntoValidation()
    {
        var validator = new FakeValidator(success: true);
        var service = CreateService(validator);

        var snapshot = await service.SetRawConnectionStringAsync(
            "Server=db;User Id=sa;Application Name=custom", password: "secret!");
        var result = await service.ValidateConnectionAsync();

        Assert.True(result.Success);
        var built = new SqlConnectionStringBuilder(validator.LastConnectionString);
        Assert.Equal("secret!", built.Password);
        Assert.Equal("custom", built.ApplicationName);

        // 保持される接続文字列（進行状態）にパスワードは混入しない。
        Assert.Equal(PromotionConnectionInputMode.Raw, snapshot.InputMode);
        Assert.DoesNotContain("secret!", snapshot.RawConnectionString, StringComparison.Ordinal);
        Assert.True(snapshot.HasPassword);
    }

    // ---- 資格情報の統治（configuration.md §5——パスワードのみ破棄・進行状態は保持） ----

    [Fact]
    public async Task InactivityTimeout_DiscardsPasswordAndValidation_ButKeepsFormAndProgress()
    {
        var service = CreateService(new FakeValidator(success: true));
        await service.SetConnectionFormAsync(SqlForm(), password: "secret!");
        await service.ValidateConnectionAsync();
        await service.ChooseOldDatabaseDisposalAsync(OldDatabaseDisposal.Evacuate, @"D:\Backup\Yagura");

        _time.Advance(WizardSessionDefaults.InactivityTimeout + TimeSpan.FromSeconds(1));

        var snapshot = await service.GetSnapshotAsync();
        Assert.False(snapshot.HasPassword);
        Assert.False(snapshot.ConnectionValidated);
        Assert.Null(snapshot.ExecuteIdempotencyToken);
        Assert.True(snapshot.CredentialReentryRequired);

        // 進行状態（接続の項目・処分の選択・退避先）は保持される（再開時に打ち直させない）。
        Assert.Equal(@"SV01\SQLEXPRESS", snapshot.Form!.ServerName);
        Assert.Equal(OldDatabaseDisposal.Evacuate, snapshot.Disposal);
        Assert.Equal(@"D:\Backup\Yagura", snapshot.EvacuationDirectory);
    }

    [Fact]
    public async Task InactivityTimeout_WindowsAuth_InvalidatesValidation_WithoutReentryRequest()
    {
        // Windows 統合認証にはパスワードが存在しない——タイムアウトで無効化されるのは検証済み
        // 状態のみで、「再入力してください」は表示しない（configuration.md §5 の粒度）。
        var service = CreateService(new FakeValidator(success: true));
        await service.SetConnectionFormAsync(WindowsForm());
        await service.ValidateConnectionAsync();

        _time.Advance(WizardSessionDefaults.InactivityTimeout + TimeSpan.FromSeconds(1));

        var snapshot = await service.GetSnapshotAsync();
        Assert.False(snapshot.ConnectionValidated);
        Assert.Null(snapshot.ExecuteIdempotencyToken);
        Assert.False(snapshot.CredentialReentryRequired);
        Assert.Equal(@"SV01\SQLEXPRESS", snapshot.Form!.ServerName);
    }

    // ---- 検証の監査と失敗分類（database.md §6.1） ----

    [Fact]
    public async Task ValidateConnection_Success_RecordsAuditWithoutSecret()
    {
        var service = CreateService(new FakeValidator(success: true));
        await service.SetConnectionFormAsync(SqlForm(), password: "secret!");

        var result = await service.ValidateConnectionAsync(operatorAddress: "127.0.0.1");

        Assert.True(result.Success);
        var snapshot = await service.GetSnapshotAsync();
        Assert.True(snapshot.ConnectionValidated);
        Assert.NotNull(snapshot.ExecuteIdempotencyToken);

        // 監査（2000 番台 ID 2002）: 資格情報を使用した事実・成否・認証方式・証明書信頼のみ。
        // パスワードは残らない（configuration.md §5）。
        var recorded = Assert.Single(_audit.RecordedEvents);
        Assert.Equal(AuditEventKind.PromotionConnectionValidated, recorded.Kind);
        Assert.Equal("127.0.0.1", recorded.RemoteAddress);
        Assert.DoesNotContain("secret!", recorded.Detail);
        Assert.Contains("成功", recorded.Detail);
        Assert.Contains("SQL Server 認証", recorded.Detail);
        Assert.Contains("サーバ証明書の信頼=無効", recorded.Detail);
    }

    [Fact]
    public async Task ValidateConnection_Failure_DoesNotEnableExecution_AndRecordsFailureKind()
    {
        var service = CreateService(new FakeValidator(success: false, PromotionConnectionFailureKind.LoginFailed));
        await service.SetConnectionFormAsync(SqlForm(), password: "wrong");

        var result = await service.ValidateConnectionAsync();

        Assert.False(result.Success);
        Assert.Equal(PromotionConnectionFailureKind.LoginFailed, result.FailureKind);
        var snapshot = await service.GetSnapshotAsync();
        Assert.False(snapshot.ConnectionValidated);
        Assert.Null(snapshot.ExecuteIdempotencyToken);

        // 失敗も監査対象（実行された管理操作——資格情報の使用があった）。分類は残すが
        // 修復 SQL の原文は残さない（database.md §6.1）。
        var recorded = Assert.Single(_audit.RecordedEvents);
        Assert.Contains("失敗", recorded.Detail);
        Assert.Contains("原因分類=ログイン失敗", recorded.Detail);
        Assert.DoesNotContain("CREATE LOGIN", recorded.Detail);
    }

    // ---- 修復 SQL の提示（database.md §5.2・§6.1——表示のみ・秘密情報を含めない） ----

    [Fact]
    public async Task ValidateConnection_LoginFailed_SqlAuth_PresentsRemediationSqlWithPlaceholderPassword()
    {
        var service = CreateService(new FakeValidator(success: false, PromotionConnectionFailureKind.LoginFailed));
        await service.SetConnectionFormAsync(SqlForm(), password: "secret!");

        var result = await service.ValidateConnectionAsync();

        Assert.NotNull(result.RemediationSql);
        Assert.Contains("CREATE LOGIN [sa]", result.RemediationSql);
        Assert.Contains("CREATE USER [sa] FOR LOGIN [sa]", result.RemediationSql);
        Assert.Contains("ALTER ROLE [db_owner] ADD MEMBER [sa]", result.RemediationSql);
        Assert.Contains("USE [Yagura]", result.RemediationSql);
        // パスワードはプレースホルダのみ（§5.2「提示 SQL は秘密情報を含まない」）。
        Assert.Contains(PromotionRemediationSql.PasswordPlaceholder, result.RemediationSql);
        Assert.DoesNotContain("secret!", result.RemediationSql);
        // db_owner の暫定性の注記（PR #102 ペルソナレビュー 田中/クリス）。
        Assert.Contains("導入時", result.RemediationSql);
    }

    [Fact]
    public async Task ValidateConnection_LoginFailed_WindowsAuth_UsesServiceAccountInRemediationSql()
    {
        var service = CreateService(new FakeValidator(success: false, PromotionConnectionFailureKind.LoginFailed));
        await service.SetConnectionFormAsync(WindowsForm());

        var result = await service.ValidateConnectionAsync();

        Assert.NotNull(result.RemediationSql);
        Assert.Contains($"CREATE LOGIN [{ServiceAccount}] FROM WINDOWS;", result.RemediationSql);
        Assert.DoesNotContain("PASSWORD", result.RemediationSql);
    }

    [Fact]
    public async Task ValidateConnection_DatabaseNotFound_PresentsCreateDatabaseSql()
    {
        var service = CreateService(new FakeValidator(success: false, PromotionConnectionFailureKind.DatabaseNotFound));
        await service.SetConnectionFormAsync(WindowsForm());

        var result = await service.ValidateConnectionAsync();

        Assert.NotNull(result.RemediationSql);
        Assert.Contains("CREATE DATABASE [Yagura];", result.RemediationSql);
        Assert.Contains($"CREATE USER [{ServiceAccount}]", result.RemediationSql);
    }

    [Theory]
    [InlineData(PromotionConnectionFailureKind.CertificateNotTrusted)]
    [InlineData(PromotionConnectionFailureKind.ServerUnreachable)]
    [InlineData(PromotionConnectionFailureKind.Unclassified)]
    public async Task ValidateConnection_NonSqlRemediableFailure_DoesNotPresentSql(PromotionConnectionFailureKind kind)
    {
        // 分類が SQL で解決しない・分類不能の失敗に修復 SQL を断定提示しない（database.md §6.1
        // の安全側）。
        var service = CreateService(new FakeValidator(success: false, kind));
        await service.SetConnectionFormAsync(WindowsForm());

        var result = await service.ValidateConnectionAsync();

        Assert.Equal(kind, result.FailureKind);
        Assert.Null(result.RemediationSql);
    }

    [Fact]
    public async Task ValidateConnection_RawModeWithoutDatabase_DoesNotPresentSql()
    {
        // 直接入力でログイン名・DB 名が特定できない場合は修復 SQL を生成しない（断定提示しない）。
        var service = CreateService(new FakeValidator(success: false, PromotionConnectionFailureKind.LoginFailed));
        await service.SetRawConnectionStringAsync("Server=db;User Id=sa", password: "secret!");

        var result = await service.ValidateConnectionAsync();

        Assert.Equal(PromotionConnectionFailureKind.LoginFailed, result.FailureKind);
        Assert.Null(result.RemediationSql);
    }

    // ---- 旧 DB 処分の選択と退避先（database.md §6.1） ----

    [Fact]
    public async Task ChooseDisposal_Evacuate_RequiresFullyQualifiedPath()
    {
        var service = CreateService(new FakeValidator(success: true));

        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ChooseOldDatabaseDisposalAsync(OldDatabaseDisposal.Evacuate));
        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ChooseOldDatabaseDisposalAsync(OldDatabaseDisposal.Evacuate, @"backup\yagura"));

        var snapshot = await service.ChooseOldDatabaseDisposalAsync(OldDatabaseDisposal.Evacuate, @"D:\Backup\Yagura");
        Assert.Equal(OldDatabaseDisposal.Evacuate, snapshot.Disposal);
        Assert.Equal(@"D:\Backup\Yagura", snapshot.EvacuationDirectory);
    }

    [Fact]
    public async Task ChooseDisposal_Delete_ClearsEvacuationDirectory()
    {
        var service = CreateService(new FakeValidator(success: true));
        await service.ChooseOldDatabaseDisposalAsync(OldDatabaseDisposal.Evacuate, @"D:\Backup\Yagura");

        var snapshot = await service.ChooseOldDatabaseDisposalAsync(OldDatabaseDisposal.Delete);

        Assert.Equal(OldDatabaseDisposal.Delete, snapshot.Disposal);
        Assert.Null(snapshot.EvacuationDirectory);
    }

    // ---- 切替実行（DPAPI 保存・監査・冪等性） ----

    [Fact]
    public async Task Execute_WithoutDisposalChoice_IsRejected()
    {
        var service = CreateService(new FakeValidator(success: true));
        await service.SetConnectionFormAsync(WindowsForm());
        await service.ValidateConnectionAsync();
        var token = (await service.GetSnapshotAsync()).ExecuteIdempotencyToken!;
        _audit.RecordedEvents.Clear();

        var result = await service.ExecuteAsync(token);

        Assert.Equal(WizardApplyOutcome.InvalidToken, result.Outcome);
        Assert.Empty(_audit.RecordedEvents);
    }

    [Fact]
    public async Task Execute_SwitchesProviderInConfiguration_RecordsAudit_AndDiscardsCredential()
    {
        var validator = new FakeValidator(success: true);
        var service = CreateService(validator);
        await service.SetConnectionFormAsync(SqlForm(trustServerCertificate: true), password: "secret!");
        await service.ValidateConnectionAsync();
        await service.ChooseOldDatabaseDisposalAsync(OldDatabaseDisposal.Evacuate, @"D:\Backup\Yagura");
        var token = (await service.GetSnapshotAsync()).ExecuteIdempotencyToken!;
        _audit.RecordedEvents.Clear();

        var result = await service.ExecuteAsync(token, operatorAddress: "127.0.0.1");

        Assert.Equal(WizardApplyOutcome.Applied, result.Outcome);
        Assert.Equal(ConfigurationApplyEffect.RestartRequired, result.RequiredEffect);

        // 設定ファイルに provider 切替が保存される（database.md §6.1 の切替を M8-4 骨格の
        // 実効——サービス再起動反映——として保存する）。
        var configPath = Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName);
        using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
        var storage = document.RootElement.GetProperty("Storage");
        Assert.Equal("sqlserver", storage.GetProperty("Provider").GetString());

        // 接続文字列は常に DPAPI 暗号化表現（dpapi:<Base64>）で保存される——設定ファイルに
        // 平文パスワードは書かれない（configuration.md §2。ADR-0004 決定 5）。復号すると
        // 検証に使ったのと同じ組み立て済み接続文字列に戻る。
        var storedConnectionString = storage.GetProperty("SqlServer").GetProperty("ConnectionString").GetString();
        Assert.NotNull(storedConnectionString);
        Assert.StartsWith("dpapi:", storedConnectionString, StringComparison.Ordinal);
        Assert.DoesNotContain("secret!", File.ReadAllText(configPath), StringComparison.Ordinal);
        Assert.True(DpapiConnectionStringProtector.TryUnprotect(storedConnectionString, out var decrypted));
        Assert.Equal(validator.LastConnectionString, decrypted);

        // 監査（2000 番台 ID 2003）: 接続文字列・パスワードは記録しない。退避先と証明書信頼の
        // 選択値は記録する（database.md §6.1）。
        var recorded = Assert.Single(_audit.RecordedEvents);
        Assert.Equal(AuditEventKind.PromotionExecuted, recorded.Kind);
        Assert.DoesNotContain("secret!", recorded.Detail);
        Assert.Contains("退避", recorded.Detail);
        Assert.Contains(@"D:\Backup\Yagura", recorded.Detail);
        Assert.Contains("サーバ証明書の信頼=有効", recorded.Detail);

        // 完了に伴い資格情報は破棄される（configuration.md §5「終了時に破棄」）。
        var snapshot = await service.GetSnapshotAsync();
        Assert.True(snapshot.Executed);
        Assert.False(snapshot.HasPassword);
    }

    [Fact]
    public async Task Execute_SameTokenTwice_DoesNotApplyTwice()
    {
        var service = CreateService(new FakeValidator(success: true));
        await service.SetConnectionFormAsync(WindowsForm());
        await service.ValidateConnectionAsync();
        await service.ChooseOldDatabaseDisposalAsync(OldDatabaseDisposal.Delete);
        var token = (await service.GetSnapshotAsync()).ExecuteIdempotencyToken!;
        _audit.RecordedEvents.Clear();

        var first = await service.ExecuteAsync(token);
        var second = await service.ExecuteAsync(token);

        Assert.Equal(WizardApplyOutcome.Applied, first.Outcome);
        Assert.Equal(WizardApplyOutcome.AlreadyApplied, second.Outcome);
        Assert.Single(_audit.RecordedEvents);
    }

    // ---- ヘルパー ----

    private static PromotionConnectionForm WindowsForm() => new(
        ServerName: @"SV01\SQLEXPRESS",
        DatabaseName: "Yagura",
        AuthenticationMode: PromotionAuthenticationMode.WindowsIntegrated,
        UserName: null,
        TrustServerCertificate: false);

    private static PromotionConnectionForm SqlForm(bool trustServerCertificate = false) => new(
        ServerName: @"SV01\SQLEXPRESS",
        DatabaseName: "Yagura",
        AuthenticationMode: PromotionAuthenticationMode.SqlServer,
        UserName: "sa",
        TrustServerCertificate: trustServerCertificate);

    private PromotionWizardService CreateService(ISqlServerConnectionValidator validator) =>
        new(_dataRoot, validator, _audit, _time, ServiceAccount);

    private sealed class FakeValidator(
        bool success,
        PromotionConnectionFailureKind failureKind = PromotionConnectionFailureKind.Unclassified) : ISqlServerConnectionValidator
    {
        public string? LastConnectionString { get; private set; }

        public Task<SqlServerConnectionValidationResult> ValidateAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            LastConnectionString = connectionString;
            return Task.FromResult(success
                ? new SqlServerConnectionValidationResult(true, "接続に成功しました。")
                : new SqlServerConnectionValidationResult(false, "接続できませんでした: テスト用の失敗。", failureKind));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> RecordedEvents { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            RecordedEvents.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
