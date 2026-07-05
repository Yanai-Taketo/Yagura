using System.Text.Json;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Administration;

/// <summary>
/// <see cref="PromotionWizardService"/> の単体テスト（M8-4。Issue #71。database.md §6.1・
/// configuration.md §5）。
/// </summary>
/// <remarks>
/// 実際の SQL Server は使わない——接続検証は <see cref="ISqlServerConnectionValidator"/> の
/// テスト実装で差し替え、経路（検証 → 選択 → 実行・監査・冪等性・資格情報統治）を検証する
/// （Issue #71「実際の SQL Server がない開発機でもテストできる形」の実装形）。
/// </remarks>
public sealed class PromotionWizardServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-promotion-test-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _audit = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.Zero));

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

    [Fact]
    public async Task ValidateConnection_Success_RecordsAuditWithoutSecret()
    {
        var service = CreateService(new FakeValidator(success: true));
        await service.SetConnectionStringAsync("Server=db;User Id=sa;Password=secret!");

        var result = await service.ValidateConnectionAsync(operatorAddress: "127.0.0.1");

        Assert.True(result.Success);
        var snapshot = await service.GetSnapshotAsync();
        Assert.True(snapshot.ConnectionValidated);
        Assert.NotNull(snapshot.ExecuteIdempotencyToken);

        // 監査（2000 番台 ID 2002）: 資格情報を使用した事実と成否のみ。パスワードは残らない
        // （configuration.md §5）。
        var recorded = Assert.Single(_audit.RecordedEvents);
        Assert.Equal(AuditEventKind.PromotionConnectionValidated, recorded.Kind);
        Assert.Equal("127.0.0.1", recorded.RemoteAddress);
        Assert.DoesNotContain("secret!", recorded.Detail);
        Assert.Contains("成功", recorded.Detail);
    }

    [Fact]
    public async Task ValidateConnection_Failure_DoesNotEnableExecution()
    {
        var service = CreateService(new FakeValidator(success: false));
        await service.SetConnectionStringAsync("Server=db;User Id=sa;Password=wrong");

        var result = await service.ValidateConnectionAsync();

        Assert.False(result.Success);
        var snapshot = await service.GetSnapshotAsync();
        Assert.False(snapshot.ConnectionValidated);
        Assert.Null(snapshot.ExecuteIdempotencyToken);

        // 失敗も監査対象（実行された管理操作——資格情報の使用があった）。
        var recorded = Assert.Single(_audit.RecordedEvents);
        Assert.Contains("失敗", recorded.Detail);
    }

    [Fact]
    public async Task ValidateConnection_WithoutConnectionString_RequiresCredential()
    {
        var service = CreateService(new FakeValidator(success: true));

        var result = await service.ValidateConnectionAsync();

        Assert.False(result.Success);
        Assert.True(result.CredentialRequired);
        Assert.Empty(_audit.RecordedEvents);
    }

    [Fact]
    public async Task InactivityTimeout_DiscardsCredential_ButKeepsProgress()
    {
        // CF-3 仮値 15 分（configuration.md §5・§9）: 資格情報は破棄・再入力要求、
        // 確定済みの進行状態（処分の選択）は保持。
        var service = CreateService(new FakeValidator(success: true));
        await service.SetConnectionStringAsync("Server=db;Password=secret");
        await service.ValidateConnectionAsync();
        await service.ChooseOldDatabaseDisposalAsync(OldDatabaseDisposal.Evacuate);

        _time.Advance(WizardSessionDefaults.InactivityTimeout + TimeSpan.FromSeconds(1));

        var snapshot = await service.GetSnapshotAsync();
        Assert.False(snapshot.HasConnectionString);
        Assert.False(snapshot.ConnectionValidated);
        Assert.Null(snapshot.ExecuteIdempotencyToken);
        Assert.True(snapshot.CredentialReentryRequired);
        Assert.Equal(OldDatabaseDisposal.Evacuate, snapshot.Disposal);
    }

    [Fact]
    public async Task Execute_WithoutDisposalChoice_IsRejected()
    {
        var service = CreateService(new FakeValidator(success: true));
        await service.SetConnectionStringAsync("Server=db");
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
        var service = CreateService(new FakeValidator(success: true));
        await service.SetConnectionStringAsync("Server=db;User Id=sa;Password=secret!");
        await service.ValidateConnectionAsync();
        await service.ChooseOldDatabaseDisposalAsync(OldDatabaseDisposal.Evacuate);
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
        // 平文パスワードは書かれない（configuration.md §2。ADR-0004 決定 5）。
        var storedConnectionString = storage.GetProperty("SqlServer").GetProperty("ConnectionString").GetString();
        Assert.NotNull(storedConnectionString);
        Assert.StartsWith("dpapi:", storedConnectionString, StringComparison.Ordinal);
        Assert.DoesNotContain("secret!", storedConnectionString, StringComparison.Ordinal);
        // ファイル全体にも平文パスワードが現れないこと。
        Assert.DoesNotContain("secret!", File.ReadAllText(configPath), StringComparison.Ordinal);
        // 復号すると入力した接続文字列に戻る（同一プロセス内 round-trip——DPAPI はマシン依存のため）。
        Assert.True(DpapiConnectionStringProtector.TryUnprotect(storedConnectionString, out var decrypted));
        Assert.Equal("Server=db;User Id=sa;Password=secret!", decrypted);

        // 監査（2000 番台 ID 2003）: 接続文字列は記録しない。
        var recorded = Assert.Single(_audit.RecordedEvents);
        Assert.Equal(AuditEventKind.PromotionExecuted, recorded.Kind);
        Assert.DoesNotContain("secret!", recorded.Detail);
        Assert.Contains("退避", recorded.Detail);

        // 完了に伴い資格情報は破棄される（configuration.md §5「終了時に破棄」）。
        var snapshot = await service.GetSnapshotAsync();
        Assert.True(snapshot.Executed);
        Assert.False(snapshot.HasConnectionString);
    }

    [Fact]
    public async Task Execute_SameTokenTwice_DoesNotApplyTwice()
    {
        var service = CreateService(new FakeValidator(success: true));
        await service.SetConnectionStringAsync("Server=db");
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

    private PromotionWizardService CreateService(ISqlServerConnectionValidator validator) =>
        new(_dataRoot, validator, _audit, _time);

    private sealed class FakeValidator(bool success) : ISqlServerConnectionValidator
    {
        public Task<SqlServerConnectionValidationResult> ValidateAsync(
            string connectionString,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SqlServerConnectionValidationResult(
                success,
                success ? "接続に成功しました。" : "接続できませんでした: テスト用の失敗。"));
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
