using Microsoft.Extensions.Logging.Testing;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <see cref="ServiceAccountStartupInspector"/> の単体テスト（ADR-0015 決定 8。Issue #263）。
/// インストーラ構成記録の一回性転記（2024）と、実効実行アカウントの変化検出（2025）の
/// 「変化検出」レール（初回スキップ・基準の取り直し・破損時の安全側）を固定する。
/// SEC-14 の CI（AD なし）継続検知範囲のうち Host 側のロジックを担う（gMSA 実環境の E2E は
/// AD lab——SEC-14 (a)〜(f)——の管轄）。
/// </summary>
public sealed class ServiceAccountStartupInspectorTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-svcacct-test-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _auditRecorder = new();
    private readonly FakeLogger<ServiceAccountStartupInspector> _logger = new();

    public ServiceAccountStartupInspectorTests()
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

    private ServiceAccountStartupInspector CreateInspector() =>
        new(_dataRoot, _auditRecorder, TimeProvider.System, _logger);

    private void WriteInstallationRecord(string account = @"NT SERVICE\Yagura")
    {
        File.WriteAllLines(
            Path.Combine(_dataRoot, ServiceAccountStartupInspector.InstallationRecordFileName),
            ["[Yagura.ServiceAccount]", $"Account={account}"]);
    }

    // ---- 転記（2024。インストーラ由来転記レール——2017 と同型） ----

    [Fact]
    public async Task Transcribe_RecordsOnceAndCreatesMarker()
    {
        WriteInstallationRecord(@"YAGURA\gmsaYagura$");
        var inspector = CreateInspector();

        await inspector.TranscribeInstallationRecordOnceAsync();

        var recorded = Assert.Single(_auditRecorder.Recorded);
        Assert.Equal(AuditEventKind.ServiceAccountTranscribed, recorded.Kind);
        Assert.Contains(@"Account=YAGURA\gmsaYagura$", recorded.Detail);
        Assert.True(File.Exists(Path.Combine(_dataRoot, ServiceAccountStartupInspector.TranscribedMarkerFileName)));
    }

    [Fact]
    public async Task Transcribe_SecondCallIsNoOp()
    {
        WriteInstallationRecord();
        var inspector = CreateInspector();

        await inspector.TranscribeInstallationRecordOnceAsync();
        await inspector.TranscribeInstallationRecordOnceAsync();

        Assert.Single(_auditRecorder.Recorded);
    }

    [Fact]
    public async Task Transcribe_WithoutRecordFile_DoesNothing()
    {
        // 手動配置・MSI 以外の導入では構成記録が存在しない——転記なし・マーカーも作らない。
        var inspector = CreateInspector();

        await inspector.TranscribeInstallationRecordOnceAsync();

        Assert.Empty(_auditRecorder.Recorded);
        Assert.False(File.Exists(Path.Combine(_dataRoot, ServiceAccountStartupInspector.TranscribedMarkerFileName)));
    }

    [Fact]
    public async Task Transcribe_WithExistingMarker_DoesNothing()
    {
        WriteInstallationRecord();
        File.WriteAllText(Path.Combine(_dataRoot, ServiceAccountStartupInspector.TranscribedMarkerFileName), "done");
        var inspector = CreateInspector();

        await inspector.TranscribeInstallationRecordOnceAsync();

        Assert.Empty(_auditRecorder.Recorded);
    }

    // ---- 変化検出（2025。2019 と同じ「変化検出」レール） ----

    [Fact]
    public async Task DetectChange_FirstBoot_SkipsComparisonAndSavesBaseline()
    {
        var inspector = CreateInspector();

        await inspector.DetectAccountChangeAndRefreshAsync(@"NT SERVICE\Yagura");

        Assert.Empty(_auditRecorder.Recorded);
        var savedPath = Path.Combine(_dataRoot, ServiceAccountStartupInspector.LastAccountFileName);
        Assert.True(File.Exists(savedPath));
        Assert.Contains(@"NT SERVICE\\Yagura", File.ReadAllText(savedPath));
    }

    [Fact]
    public async Task DetectChange_SameAccount_DoesNotRecord()
    {
        var inspector = CreateInspector();
        await inspector.DetectAccountChangeAndRefreshAsync(@"NT SERVICE\Yagura");

        await inspector.DetectAccountChangeAndRefreshAsync(@"NT SERVICE\Yagura");

        Assert.Empty(_auditRecorder.Recorded);
    }

    [Fact]
    public async Task DetectChange_CaseDifferenceOnly_DoesNotRecord()
    {
        // Windows のアカウント名は大文字小文字を区別しない——表記ゆれを「切替」と誤報しない。
        var inspector = CreateInspector();
        await inspector.DetectAccountChangeAndRefreshAsync(@"NT SERVICE\Yagura");

        await inspector.DetectAccountChangeAndRefreshAsync(@"NT SERVICE\YAGURA");

        Assert.Empty(_auditRecorder.Recorded);
    }

    [Fact]
    public async Task DetectChange_DifferentAccount_RecordsOldAndNew()
    {
        var inspector = CreateInspector();
        await inspector.DetectAccountChangeAndRefreshAsync(@"NT SERVICE\Yagura");

        await inspector.DetectAccountChangeAndRefreshAsync(@"YAGURA\gmsaYagura$");

        var recorded = Assert.Single(_auditRecorder.Recorded);
        Assert.Equal(AuditEventKind.ServiceAccountChangeDetected, recorded.Kind);
        Assert.Contains(@"旧=NT SERVICE\Yagura", recorded.Detail);
        Assert.Contains(@"新=YAGURA\gmsaYagura$", recorded.Detail);
    }

    [Fact]
    public async Task DetectChange_ChangeIsReportedOnlyOnce()
    {
        // 検出後は新アカウントが基準として保存され、次回起動で重複報告しない（2019 と同型）。
        var inspector = CreateInspector();
        await inspector.DetectAccountChangeAndRefreshAsync(@"NT SERVICE\Yagura");
        await inspector.DetectAccountChangeAndRefreshAsync(@"YAGURA\gmsaYagura$");

        await inspector.DetectAccountChangeAndRefreshAsync(@"YAGURA\gmsaYagura$");

        Assert.Single(_auditRecorder.Recorded);
    }

    [Fact]
    public async Task DetectChange_CorruptedRecord_SkipsComparisonAndRewritesBaseline()
    {
        // 破損した記録は「初回起動と同じ扱い」（照合スキップ・安全側）で、今回の値で保存し直す。
        var path = Path.Combine(_dataRoot, ServiceAccountStartupInspector.LastAccountFileName);
        File.WriteAllText(path, "{ not-json !");
        var inspector = CreateInspector();

        await inspector.DetectAccountChangeAndRefreshAsync(@"YAGURA\gmsaYagura$");

        Assert.Empty(_auditRecorder.Recorded);
        Assert.Contains("gmsaYagura", File.ReadAllText(path));
    }

    // ---- 実効アカウントの解決 ----

    [Fact]
    public void ResolveEffectiveAccountName_ReturnsNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ServiceAccountStartupInspector.ResolveEffectiveAccountName()));
    }
}
