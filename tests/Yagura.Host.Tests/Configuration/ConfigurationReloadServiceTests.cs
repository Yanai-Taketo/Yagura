using Microsoft.Extensions.Logging.Testing;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <see cref="ConfigurationReloadService"/> の単体テスト（configuration.md §3。CF-4 層1。
/// Issue #262）。差分適用・未反映の累積明示・検証失敗時の旧設定継続・監査記録（2016）を固定する。
/// </summary>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class ConfigurationReloadServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-reload-test-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _auditRecorder = new();

    public ConfigurationReloadServiceTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private sealed class RecordingAuditRecorder : IAuditRecorder
    {
        public List<AuditEvent> Recorded { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private void WriteConfigurationFile(string json) =>
        File.WriteAllText(Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName), json);

    private ConfigurationReloadService CreateService(
        params ImmediateConfigurationApplier[] appliers)
    {
        // 起動時スナップショット = 現時点のファイル内容（Program.cs と同じ捕捉方法）。
        var startupOptions = YaguraConfigurationWriter.Read(_dataRoot).Options;
        return new ConfigurationReloadService(
            _dataRoot, startupOptions, appliers, _auditRecorder, logger: new FakeLogger<ConfigurationReloadService>());
    }

    [Fact]
    public async Task Reload_NoChanges_ReturnsEmptyResultWithoutAudit()
    {
        WriteConfigurationFile("""{ "Retention": { "Days": "30" } }""");
        var service = CreateService();

        var result = await service.ReloadAsync(null, null, null);

        Assert.False(result.Rejected);
        Assert.False(result.HasChanges);
        Assert.Empty(_auditRecorder.Recorded);
    }

    [Fact]
    public async Task Reload_ImmediateKeyChanged_AppliesViaApplierAndRecordsAudit()
    {
        WriteConfigurationFile("""{ "Retention": { "Days": "30" } }""");
        int? appliedDays = null;
        var service = CreateService(new ImmediateConfigurationApplier(
            ["Retention:Days", "Retention:ExecutionTimeOfDay"],
            resolved => appliedDays = resolved.RetentionDays));

        // 手編集を模す（保持期間 30 → 90）。
        WriteConfigurationFile("""{ "Retention": { "Days": "90" } }""");
        var result = await service.ReloadAsync("127.0.0.1", "windows", @"CONTOSO\admin");

        Assert.False(result.Rejected);
        Assert.Equal(new[] { "Retention:Days" }, result.ChangedKeys);
        Assert.Equal(new[] { "Retention:Days" }, result.AppliedKeys);
        Assert.Empty(result.PendingRestartKeys);
        Assert.Equal(90, appliedDays);

        // 再読み込みは管理操作——監査記録 2016（configuration.md §3）。
        var audit = Assert.Single(_auditRecorder.Recorded);
        Assert.Equal(AuditEventKind.ConfigurationReloaded, audit.Kind);
        Assert.Contains("Retention:Days", audit.Detail);
        Assert.Equal(@"CONTOSO\admin", audit.AuthenticatedPrincipal);
    }

    [Fact]
    public async Task Reload_RestartRequiredKeyChanged_ReportsPendingAndAccumulatesAcrossReloads()
    {
        WriteConfigurationFile("""{ }""");
        var service = CreateService(); // 適用の口なし——全変更が再起動待ちに落ちる

        // Viewer:HttpPort は RestartRequired（ConfigurationKeyMetadata）。
        WriteConfigurationFile("""{ "Viewer": { "HttpPort": "9100" } }""");
        var first = await service.ReloadAsync(null, null, null);

        Assert.Equal(new[] { "Viewer:HttpPort" }, first.PendingRestartKeys);
        Assert.Empty(first.AppliedKeys);

        // 別のキーをさらに変更しても、前回の再起動待ちは累積して見え続ける（§3——
        // 気づかない中途状態を作らない）。
        WriteConfigurationFile("""{ "Viewer": { "HttpPort": "9100" }, "Storage": { "SqliteFileName": "x.db" } }""");
        var second = await service.ReloadAsync(null, null, null);

        Assert.Equal(new[] { "Storage:SqliteFileName" }, second.ChangedKeys);
        Assert.Contains("Viewer:HttpPort", second.PendingRestartKeys);
        Assert.Contains("Storage:SqliteFileName", second.PendingRestartKeys);
    }

    [Fact]
    public async Task Reload_ValidationFailure_RejectsAndKeepsOldConfiguration()
    {
        WriteConfigurationFile("""{ }""");
        var applierCalled = false;
        var service = CreateService(new ImmediateConfigurationApplier(
            ["Retention:Days"], _ => applierCalled = true));

        // 受信ポート不正 = 起動失敗分類（configuration.md §1）——稼働中は適用拒否で旧設定継続。
        WriteConfigurationFile("""{ "Ingestion": { "Udp": { "Port": "not-a-port" } }, "Retention": { "Days": "90" } }""");
        var result = await service.ReloadAsync(null, null, null);

        Assert.True(result.Rejected);
        Assert.NotNull(result.RejectionReason);
        Assert.False(applierCalled, "検証失敗時は一切適用しないこと（半分だけ適用を作らない）。");
        Assert.Empty(_auditRecorder.Recorded);

        // 修正後の再読み込みでは、拒否された間の変更も含めて正しく差分検出される。
        WriteConfigurationFile("""{ "Retention": { "Days": "90" } }""");
        var recovered = await service.ReloadAsync(null, null, null);
        Assert.True(applierCalled);
        Assert.Equal(new[] { "Retention:Days" }, recovered.AppliedKeys);
    }

    [Fact]
    public async Task Reload_InvalidImmediateValue_AppliesFallbackAndReportsWarning()
    {
        WriteConfigurationFile("""{ }""");
        int? appliedDays = 12345;
        var service = CreateService(new ImmediateConfigurationApplier(
            ["Retention:Days"], resolved => appliedDays = resolved.RetentionDays));

        // 不正値は「削除しない」（null）へフォールバックして適用され、警告が結果に載る。
        WriteConfigurationFile("""{ "Retention": { "Days": "-5" } }""");
        var result = await service.ReloadAsync(null, null, null);

        Assert.False(result.Rejected);
        Assert.Null(appliedDays);
        Assert.Contains(result.WarningMessages, w => w.Contains("Retention:Days"));
    }

    [Fact]
    public async Task Reload_UnknownKey_IsReportedInResult()
    {
        WriteConfigurationFile("""{ }""");
        var service = CreateService();

        WriteConfigurationFile("""{ "Retentoin": { "Days": "90" } }""");
        var result = await service.ReloadAsync(null, null, null);

        Assert.Contains("Retentoin:Days", result.UnknownKeys);
    }
}
