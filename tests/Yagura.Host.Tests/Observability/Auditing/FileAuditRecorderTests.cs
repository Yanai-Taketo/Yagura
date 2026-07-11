using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Observability.Auditing;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Diagnostics;

namespace Yagura.Host.Tests.Observability.Auditing;

/// <summary>
/// <see cref="FileAuditRecorder"/> の単体テスト（M6-2。Issue #52。security.md §4.1・§4.2）。
/// </summary>
/// <remarks>
/// 検証観点: (1) アプリ記録ファイル（追記型 JSON Lines）への書き込み、(2) Windows イベントログ
/// 併記経路（<see cref="ILogger"/> 呼び出し。<c>EventId</c> 3001・警告レベルであること）、
/// (3) ファイル書き込み失敗（不正なパス等）でも例外を投げず、拒否カウンタとは独立の
/// 監査記録書き込み失敗カウンタへ計上する多段の最小実装（ただしイベントログ経路が
/// 生きていれば「両方失敗」ではないため、カウンタは計上されないことも確認する）。
/// </remarks>
public sealed class FileAuditRecorderTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-audit-test-{Guid.NewGuid():N}");

    public FileAuditRecorderTests()
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
                // ベストエフォート（他テストと同じ判断）。
            }
        }
    }

    private static AuditEvent CreateSampleEvent() => new(
        OccurredAt: new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero),
        Kind: AuditEventKind.ViewerListenerAdminRequestRejected,
        RemoteAddress: "203.0.113.5",
        RemotePort: 54321,
        AttemptedPath: "/admin",
        ReachedListenerPort: 8514);

    [Fact]
    public async Task RecordAsync_WritesLineToAuditFile()
    {
        var logger = new FakeLogger();
        using var metrics = new WebGuardMetrics();
        var recorder = new FileAuditRecorder(_dataRoot, logger, metrics);

        await recorder.RecordAsync(CreateSampleEvent());

        var filePath = Path.Combine(_dataRoot, FileAuditRecorder.DirectoryName, FileAuditRecorder.FileName);
        Assert.True(File.Exists(filePath), $"監査記録ファイルが作成されていない: {filePath}");

        var lines = await File.ReadAllLinesAsync(filePath);
        var line = Assert.Single(lines);
        Assert.Contains("203.0.113.5", line);
        Assert.Contains("/admin", line);
        Assert.Contains("ViewerListenerAdminRequestRejected", line);
    }

    [Fact]
    public async Task RecordAsync_AppendsMultipleEventsAsSeparateLines()
    {
        var logger = new FakeLogger();
        using var metrics = new WebGuardMetrics();
        var recorder = new FileAuditRecorder(_dataRoot, logger, metrics);

        await recorder.RecordAsync(CreateSampleEvent());
        await recorder.RecordAsync(CreateSampleEvent() with { AttemptedPath = "/admin/users" });

        var filePath = Path.Combine(_dataRoot, FileAuditRecorder.DirectoryName, FileAuditRecorder.FileName);
        var lines = await File.ReadAllLinesAsync(filePath);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task RecordAsync_WritesWarningLevelEventLogEntryWithEventId3001()
    {
        var logger = new FakeLogger();
        using var metrics = new WebGuardMetrics();
        var recorder = new FileAuditRecorder(_dataRoot, logger, metrics);

        await recorder.RecordAsync(CreateSampleEvent());

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, record.Level);
        Assert.Equal(AuditEventIds.ViewerListenerAdminRequestRejected.Id, record.Id.Id);
        Assert.Contains("203.0.113.5", record.Message);
    }

    [Theory]
    [InlineData(AuditEventKind.ViewerListenerAdminRequestRejected, 3001, "閲覧リスナへの管理操作を拒否")]
    [InlineData(AuditEventKind.ConfigurationSaved, 2001, "設定変更を適用")]
    [InlineData(AuditEventKind.PromotionConnectionValidated, 2002, "本番昇格の接続検証を実施")]
    [InlineData(AuditEventKind.PromotionExecuted, 2003, "本番昇格を実行")]
    [InlineData(AuditEventKind.CircuitDisconnected, 2004, "circuit を切断")]
    [InlineData(AuditEventKind.CircuitOriginRejected, 3002, "circuit 確立要求の origin 検証で拒否")]
    [InlineData(AuditEventKind.AdminAuthorizationDenied, 3006, "認証成功後に管理者権限がなくアクセスを拒否")]
    public async Task RecordAsync_EventLogMessage_UsesJapaneseDescriptionAndPreservesEventId(
        AuditEventKind kind,
        int expectedEventId,
        string expectedDescription)
    {
        // 2026-07-06 イベントログ日本語化: {Kind} の英語 enum 名がイベントログ本文に漏れず、
        // 種別ごとの日本語説明に置き換わっていること。イベント ID・種別の対応（additive-only。
        // security.md §4.3）が本変更で壊れていないことも合わせて固定する。
        var logger = new FakeLogger();
        using var metrics = new WebGuardMetrics();
        var recorder = new FileAuditRecorder(_dataRoot, logger, metrics);

        await recorder.RecordAsync(new AuditEvent(
            OccurredAt: new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero),
            Kind: kind,
            RemoteAddress: "203.0.113.5",
            RemotePort: 54321));

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(expectedEventId, record.Id.Id);
        Assert.Contains(expectedDescription, record.Message);
        Assert.DoesNotContain(kind.ToString(), record.Message);
    }

    [Fact]
    public async Task RecordAsync_AuditFileLine_StillUsesEnglishEnumNameForKind()
    {
        // アプリ記録ファイル（JSON Lines）の Kind フィールドは、イベントログ本文の日本語化とは
        // 独立して英語 enum 名のまま維持する（外部ツールによる機械的解析対象——
        // AuditEventDescriptions のコメント参照）。
        var logger = new FakeLogger();
        using var metrics = new WebGuardMetrics();
        var recorder = new FileAuditRecorder(_dataRoot, logger, metrics);

        await recorder.RecordAsync(CreateSampleEvent());

        var filePath = Path.Combine(_dataRoot, FileAuditRecorder.DirectoryName, FileAuditRecorder.FileName);
        var line = Assert.Single(await File.ReadAllLinesAsync(filePath));
        Assert.Contains("\"Kind\":\"ViewerListenerAdminRequestRejected\"", line);
    }

    [Fact]
    public async Task RecordAsync_FileWriteFails_StillWritesEventLogAndDoesNotThrow()
    {
        // ADR-0004 決定 7「監査記録の書き込み不能は要求処理を妨げない」: アプリ記録ファイルへの
        // 書き込みが失敗する状況（ここではディレクトリと同名のファイルを事前に置き、
        // Directory.CreateDirectory を失敗させることで再現する）でも、例外を投げず、
        // イベントログ経路（生きている）は書き込まれることを確認する。
        var conflictingFilePath = Path.Combine(_dataRoot, FileAuditRecorder.DirectoryName);
        File.WriteAllText(conflictingFilePath, "occupied");

        var logger = new FakeLogger();
        using var metrics = new WebGuardMetrics();
        var recorder = new FileAuditRecorder(_dataRoot, logger, metrics);

        var exception = await Record.ExceptionAsync(() => recorder.RecordAsync(CreateSampleEvent()));

        Assert.Null(exception);

        // イベントログ経路(1件目=書込失敗の警告、2件目=監査事象そのもの)は生きているため、
        // 監査記録書き込み失敗カウンタ(yagura.web.audit.write_failed)へは計上されない
        // （アプリ記録ファイルとイベントログの両方が失敗した場合のみ計上する設計）。
        Assert.Contains(
            logger.Collector.GetSnapshot(),
            record => record.Id.Id == AuditEventIds.ViewerListenerAdminRequestRejected.Id);
    }

    [Fact]
    public async Task RecordAsync_CreatesAuditSubdirectoryUnderDataRoot()
    {
        var logger = new FakeLogger();
        using var metrics = new WebGuardMetrics();
        var recorder = new FileAuditRecorder(_dataRoot, logger, metrics);

        await recorder.RecordAsync(CreateSampleEvent());

        var auditDirectory = Path.Combine(_dataRoot, FileAuditRecorder.DirectoryName);
        Assert.True(Directory.Exists(auditDirectory));
    }

    [Fact]
    public async Task RecordAsync_AdminOperation_WritesInformationLevelWith2000BandIdAndDetail()
    {
        // M8-4（Issue #71）: 2000 番台（管理操作の監査）はレベル「情報」で併記される
        // （security.md §4.3 の区画割当——3000 番台の警告と機械的に区別される）。
        // 要約（Detail）はファイル行・イベントログ本文の両方に残る。
        var logger = new FakeLogger();
        using var metrics = new WebGuardMetrics();
        var recorder = new FileAuditRecorder(_dataRoot, logger, metrics);

        await recorder.RecordAsync(new AuditEvent(
            OccurredAt: new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero),
            Kind: AuditEventKind.ConfigurationSaved,
            RemoteAddress: "127.0.0.1",
            RemotePort: null,
            Detail: "変更キー: Retention:Days"));

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, record.Level);
        Assert.Equal(AuditEventIds.ConfigurationSaved.Id, record.Id.Id);
        Assert.Contains("Retention:Days", record.Message);

        var filePath = Path.Combine(_dataRoot, FileAuditRecorder.DirectoryName, FileAuditRecorder.FileName);
        var line = Assert.Single(await File.ReadAllLinesAsync(filePath));
        Assert.Contains("ConfigurationSaved", line);
        Assert.Contains("2001", line);
        Assert.Contains("Retention:Days", line);
    }
}
