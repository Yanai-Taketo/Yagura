using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <see cref="StartupConfigurationInspector"/> / <see cref="LastAppliedConfigurationSnapshotStore"/>
/// の単体テスト（Issue #329。security.md §4.1・configuration.md §3）。検証観点:
/// (1) 初回起動（スナップショット不在）は照合スキップ + 基準の新規保存、
/// (2) 差分なしは監査を出さない、(3) 差分ありは監査 2019 を 1 件・変更キー名のみ・
/// 前後値を含めない、(4) 照合後に基準が取り直され次回起動で重複報告しない、
/// (5) スナップショット破損時は照合スキップ + 基準の取り直し（起動を妨げない）。
/// </summary>
public sealed class StartupConfigurationInspectorTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-startup-inspect-test-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _audit = new();

    public StartupConfigurationInspectorTests()
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

    private StartupConfigurationInspector CreateInspector() => new(
        _dataRoot,
        _audit,
        new FakeTimeProvider(new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero)),
        new FakeLogger<StartupConfigurationInspector>());

    private void WriteConfigurationFile(string json) =>
        File.WriteAllText(Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName), json);

    /// <summary>Program.cs と同じ捕捉方法で起動時の生 options を得る。</summary>
    private YaguraConfigurationOptions ReadStartupOptions() =>
        YaguraConfigurationWriter.Read(_dataRoot).Options;

    private string SnapshotPath =>
        Path.Combine(_dataRoot, LastAppliedConfigurationSnapshotStore.FileName);

    [Fact]
    public async Task Inspect_FirstBoot_SkipsComparisonAndSavesBaseline()
    {
        WriteConfigurationFile("""{ "Retention": { "Days": "30" } }""");

        await CreateInspector().InspectAndRefreshSnapshotAsync(ReadStartupOptions());

        // 初回起動: 照合基準が存在しないため監査は出さず、今回の設定を基準として保存する。
        Assert.Empty(_audit.Recorded);
        Assert.True(File.Exists(SnapshotPath));
    }

    [Fact]
    public async Task Inspect_NoChangesSinceLastRun_DoesNotRecordAudit()
    {
        WriteConfigurationFile("""{ "Retention": { "Days": "30" } }""");
        LastAppliedConfigurationSnapshotStore.TrySave(_dataRoot, ReadStartupOptions());

        await CreateInspector().InspectAndRefreshSnapshotAsync(ReadStartupOptions());

        Assert.Empty(_audit.Recorded);
    }

    [Fact]
    public async Task Inspect_ChangedWhileStopped_RecordsAudit2019WithKeyNamesOnly()
    {
        // 前回稼働時の基準（保持 30 日）を保存した後、停止中の手編集（30 → 90）を模す。
        WriteConfigurationFile("""{ "Retention": { "Days": "30" } }""");
        LastAppliedConfigurationSnapshotStore.TrySave(_dataRoot, ReadStartupOptions());
        WriteConfigurationFile("""{ "Retention": { "Days": "90" } }""");

        await CreateInspector().InspectAndRefreshSnapshotAsync(ReadStartupOptions());

        var audit = Assert.Single(_audit.Recorded);
        Assert.Equal(AuditEventKind.StartupConfigurationChangeDetected, audit.Kind);
        Assert.Contains("Retention:Days", audit.Detail);
        // 変更キー名のみ——前後値を含めない（2016 と同粒度。秘密値の混入を構造的に避ける）。
        Assert.DoesNotContain("30", audit.Detail);
        Assert.DoesNotContain("90", audit.Detail);
        // 起動時の自動照合——操作者情報は持たない。
        Assert.Null(audit.RemoteAddress);
        Assert.Null(audit.AuthenticationScheme);
        Assert.Null(audit.AuthenticatedPrincipal);
    }

    [Fact]
    public async Task Inspect_RefreshesBaseline_SoNextBootDoesNotRepeatTheReport()
    {
        WriteConfigurationFile("""{ "Retention": { "Days": "30" } }""");
        LastAppliedConfigurationSnapshotStore.TrySave(_dataRoot, ReadStartupOptions());
        WriteConfigurationFile("""{ "Retention": { "Days": "90" } }""");

        await CreateInspector().InspectAndRefreshSnapshotAsync(ReadStartupOptions());
        Assert.Single(_audit.Recorded);

        // 次回起動を模す: 基準は取り直されているため、同じ差分を重複報告しない。
        await CreateInspector().InspectAndRefreshSnapshotAsync(ReadStartupOptions());
        Assert.Single(_audit.Recorded);
    }

    [Fact]
    public async Task Inspect_CorruptSnapshot_SkipsComparisonAndRewritesBaseline()
    {
        WriteConfigurationFile("""{ "Retention": { "Days": "30" } }""");
        File.WriteAllText(SnapshotPath, "{ not valid json !!");

        await CreateInspector().InspectAndRefreshSnapshotAsync(ReadStartupOptions());

        // 破損は初回起動と同じ扱い（照合スキップ + 基準の取り直し）——起動を妨げない。
        Assert.Empty(_audit.Recorded);

        // 取り直した基準は有効な JSON として読める（次回起動から照合が再開する）。
        Assert.NotNull(LastAppliedConfigurationSnapshotStore.TryRead(_dataRoot));
    }
}
