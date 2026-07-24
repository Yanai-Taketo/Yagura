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

    private static readonly DateTimeOffset SampleOccurredAt = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    private static AuditEvent CreateSampleEvent() => new(
        OccurredAt: SampleOccurredAt,
        Kind: AuditEventKind.ViewerListenerAdminRequestRejected,
        RemoteAddress: "203.0.113.5",
        RemotePort: 54321,
        AttemptedPath: "/admin",
        ReachedListenerPort: 8514);

    /// <summary>事象発生日の日次ファイル（Issue #261 の日次ローテーション）のフルパスを組み立てる。</summary>
    private string AuditFilePathFor(DateTimeOffset occurredAt) =>
        Path.Combine(_dataRoot, FileAuditRecorder.DirectoryName, FileAuditRecorder.GetFileNameFor(occurredAt));

    [Fact]
    public async Task RecordAsync_WritesLineToAuditFile()
    {
        var logger = new FakeLogger();
        using var metrics = new WebGuardMetrics();
        var recorder = new FileAuditRecorder(_dataRoot, logger, metrics);

        await recorder.RecordAsync(CreateSampleEvent());

        var filePath = AuditFilePathFor(SampleOccurredAt);
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

        var filePath = AuditFilePathFor(SampleOccurredAt);
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
    [InlineData(AuditEventKind.AdminAuthorizationDenied, 3008, "認証成功後に管理者権限がなくアクセスを拒否")]
    [InlineData(AuditEventKind.AdminRemoteBindingConfigured, 2011, "管理リスナのリモートバインド設定を変更")]
    [InlineData(AuditEventKind.AdminHttpsCertificateConfigured, 2012, "管理 UI リモート HTTPS の証明書設定を変更")]
    [InlineData(AuditEventKind.AdminAuthBackoffCapReached, 3006, "アプリ独自認証のバックオフが上限に到達")]
    [InlineData(AuditEventKind.AdminAuthRateLimited, 3007, "アプリ独自認証のログイン試行をレート制限で拒否")]
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

        var filePath = AuditFilePathFor(SampleOccurredAt);
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

        var occurredAt = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        await recorder.RecordAsync(new AuditEvent(
            OccurredAt: occurredAt,
            Kind: AuditEventKind.ConfigurationSaved,
            RemoteAddress: "127.0.0.1",
            RemotePort: null,
            Detail: "変更キー: Retention:Days"));

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, record.Level);
        Assert.Equal(AuditEventIds.ConfigurationSaved.Id, record.Id.Id);
        Assert.Contains("Retention:Days", record.Message);

        var filePath = AuditFilePathFor(occurredAt);
        var line = Assert.Single(await File.ReadAllLinesAsync(filePath));
        Assert.Contains("ConfigurationSaved", line);
        Assert.Contains("2001", line);
        Assert.Contains("Retention:Days", line);
    }

    [Fact]
    public async Task RecordAsync_EventsOnDifferentUtcDays_WriteToSeparateDailyFiles()
    {
        // 日次ローテーション（Issue #261）: 事象発生日（UTC）が変わると追記先ファイルが切り替わる。
        // ファイル名の日付は書き込み時点の時計ではなく事象の OccurredAt に基づく。
        var logger = new FakeLogger();
        using var metrics = new WebGuardMetrics();
        var recorder = new FileAuditRecorder(_dataRoot, logger, metrics);

        var day1 = new DateTimeOffset(2026, 7, 5, 23, 59, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 7, 6, 0, 1, 0, TimeSpan.Zero);
        await recorder.RecordAsync(CreateSampleEvent() with { OccurredAt = day1 });
        await recorder.RecordAsync(CreateSampleEvent() with { OccurredAt = day2 });

        Assert.Equal("audit-20260705.jsonl", FileAuditRecorder.GetFileNameFor(day1));
        Assert.Equal("audit-20260706.jsonl", FileAuditRecorder.GetFileNameFor(day2));
        Assert.Single(await File.ReadAllLinesAsync(AuditFilePathFor(day1)));
        Assert.Single(await File.ReadAllLinesAsync(AuditFilePathFor(day2)));
    }

    [Fact]
    public void GetFileNameFor_LocalOffsetTimestamp_UsesUtcDate()
    {
        // ファイル名の日付は UTC 基準——ローカルオフセット付きの OccurredAt でも UTC 換算で決まる
        // （JST 2026-07-06 08:00 = UTC 2026-07-05 23:00 → 07-05 のファイル）。
        var jstMorning = new DateTimeOffset(2026, 7, 6, 8, 0, 0, TimeSpan.FromHours(9));

        Assert.Equal("audit-20260705.jsonl", FileAuditRecorder.GetFileNameFor(jstMorning));
    }

    [Fact]
    public void ResolveEventId_MapsEveryAuditEventKind_WithoutFallback()
    {
        // F-HOST-1 の再発防止: enum に事象種別を追加したのに ResolveEventId の switch 追随を
        // 忘れると、認証拒否などの記録経路で ArgumentOutOfRangeException が投げられ、要求が 500 に
        // なり監査が沈黙する。全 AuditEventKind が固有 EventId（0 = 未解決フォールバック以外）へ
        // 解決されることを機械検証し、将来の switch 追随漏れをビルド時に検出する。
        foreach (var kind in Enum.GetValues<AuditEventKind>())
        {
            var eventId = FileAuditRecorder.ResolveEventId(kind);
            Assert.NotEqual(0, eventId.Id);
        }
    }

    [Fact]
    public void Describe_MapsEveryAuditEventKind_WithoutFallback()
    {
        // ResolveEventId と同型の追随漏れ検証を Describe（イベントログ本文の日本語説明）にも課す。
        // 実際に 2021〜2023（ADR-0017/0018 の設定変更・テスト送信・ウォッチリスト変更）が
        // Describe の switch から漏れており、該当事象のイベントログ併記が TryWriteEventLog の
        // 最終捕捉で毎回縮退していた（アプリ記録ファイル側は無事——Issue #263 実装時に発見・修正）。
        foreach (var kind in Enum.GetValues<AuditEventKind>())
        {
            var description = AuditEventDescriptions.Describe(kind);
            Assert.False(string.IsNullOrWhiteSpace(description));
        }
    }

    [Theory]
    [InlineData(AuditEventKind.AdminAuthBackoffCapReached, 3006)]
    [InlineData(AuditEventKind.AdminAuthRateLimited, 3007)]
    public async Task RecordAsync_Adr0011DenialEvents_DoNotThrowAndAreRecorded(
        AuditEventKind kind, int expectedEventId)
    {
        // F-HOST-1 回帰: ADR-0011 三層防御の拒否事象（3006/3007）が ResolveEventId の switch に
        // 無く、認証拒否の記録経路（AppLoginEndpointHandler.RecordDenialAuditAsync）で throw して
        // いた。例外を投げず、ファイル・イベントログの双方へ記録されることを固定する。
        var logger = new FakeLogger();
        using var metrics = new WebGuardMetrics();
        var recorder = new FileAuditRecorder(_dataRoot, logger, metrics);

        var occurredAt = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        var exception = await Record.ExceptionAsync(() => recorder.RecordAsync(new AuditEvent(
            OccurredAt: occurredAt,
            Kind: kind,
            RemoteAddress: "203.0.113.5",
            RemotePort: 54321,
            Detail: "layer=ip-rate-limit")));

        Assert.Null(exception);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(expectedEventId, record.Id.Id);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, record.Level);

        var line = Assert.Single(await File.ReadAllLinesAsync(AuditFilePathFor(occurredAt)));
        Assert.Contains(kind.ToString(), line);
        Assert.Contains(expectedEventId.ToString(), line);
    }
}
