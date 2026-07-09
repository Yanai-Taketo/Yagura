using Microsoft.Extensions.DependencyInjection;
using Yagura.Abstractions.Observability;
using Yagura.Storage;
using Yagura.Web.Components.Common;
using Yagura.Web.Components.Layout;
using Yagura.Web.Components.Pages;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 閲覧 3 画面（ダッシュボード / ログ検索 / システム状態）+ 共通骨格の表示確認
/// （M8-3。Issue #70。ui.md §4）。M8-2 の共通コンポーネントテストと同形式——
/// HtmlRenderer による実描画（prerender 相当。<see cref="CommonComponentRenderHarness"/>）で
/// 主要な表示状態（空・データあり・受信断あり・保持地平・OS ゲージ注記等）を検証する。
/// データ源はフェイク（ILogStore / IYaguraSystemStatusReader）を DI へ差し込む。
/// </summary>
public sealed class ViewerPageRenderTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    // ---- ダッシュボード（ui.md §4・§5.1・§5.3。UI-4 無音化検出） ----

    [Fact]
    public async Task Dashboard_Empty_ShowsEmptyStateWithListenerPortsAndRetentionNotice()
    {
        var store = new FakeLogStore();
        var reader = new FakeStatusReader
        {
            RetentionDays = 30,
            Listeners = [new YaguraListenerEndpoint("UDP", 514), new YaguraListenerEndpoint("TCP", 6514)],
        };

        var html = await RenderPageAsync<Dashboard>(store, reader);

        // ログ未着の空状態: 次の行動 + 受信先のコピー可能表示（ui.md §3.1 空状態規約）
        Assert.Contains(UiText.NoLogsEmptyTitle, html, StringComparison.Ordinal);
        Assert.Contains(UiText.NoLogsEmptyNextAction, html, StringComparison.Ordinal);
        Assert.Contains("UDP 受信ポート", html, StringComparison.Ordinal);
        Assert.Contains(">514<", html, StringComparison.Ordinal);

        // 保持期間の常時明示（database.md §3・ui.md §5.3 の確定文言）
        Assert.Contains("30 日より古いログは自動的に削除されます", html, StringComparison.Ordinal);

        // 受信 0 件の時間軸チャートの注記
        Assert.Contains(UiText.TimelineNoData, html, StringComparison.Ordinal);

        // 現在値カード群（スプール・退避・取りこぼし・保存件数）
        Assert.Contains(UiText.StatSpoolUsage, html, StringComparison.Ordinal);
        Assert.Contains(UiText.StatLossTotal, html, StringComparison.Ordinal);
        Assert.Contains(UiText.StatStoredRecords, html, StringComparison.Ordinal);

        // 重大度分布・Top talkers（受信 0 件時は空データの注記。Issue #159）
        Assert.Contains(UiText.SeverityDistributionTitle, html, StringComparison.Ordinal);
        Assert.Contains(UiText.SeverityDistributionNoData, html, StringComparison.Ordinal);
        Assert.Contains(UiText.TopTalkersTitle, html, StringComparison.Ordinal);
        Assert.Contains(UiText.TopTalkersNoData, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_WithSeverityDistributionAndTopTalkers_ShowsBothSections()
    {
        // 重大度分布（平常時からの逸脱検知）と Top talkers（受信量上位。フラッディング検知）
        // の 2 視点（Issue #159）。両集計は受信量推移と同じ窓（直近 1 時間）に限定される
        // ——フェイクは窓を無視してそのまま返すため、値の突合はここでは表示内容の確認に留める。
        var store = new FakeLogStore
        {
            SeverityDistribution =
            [
                new SeverityCount(Severity: 3, Count: 12),
                new SeverityCount(Severity: 6, Count: 40),
                new SeverityCount(Severity: null, Count: 2), // PRI 解析失敗バケット
            ],
            TopTalkers =
            [
                new SourceActivity("192.0.2.50", DateTimeOffset.UtcNow, RecordCount: 9_000),
                new SourceActivity("192.0.2.51", DateTimeOffset.UtcNow, RecordCount: 120),
            ],
        };
        var reader = new FakeStatusReader { RetentionDays = 30 };

        var html = await RenderPageAsync<Dashboard>(store, reader);

        // 重大度分布: 短形ラベル + 件数（YaguraSeverityChip 経由の表示。ui.md §4 の色対応を流用）
        Assert.Contains(UiText.SeverityDistributionTitle, html, StringComparison.Ordinal);
        Assert.Contains(UiText.SeverityShortLabels[3], html, StringComparison.Ordinal);
        Assert.Contains(UiText.SeverityShortLabels[6], html, StringComparison.Ordinal);
        // PRI 解析失敗バケット（解析失敗の事実を隠さない——ui.md §5.3 と同じ向き）
        Assert.Contains(UiText.SeverityDistributionUnparsedLabel, html, StringComparison.Ordinal);
        Assert.Contains("40", html, StringComparison.Ordinal);

        // Top talkers: 受信量降順の送信元表 + 検索への導線（既存の無音化検出表とは別視点）
        Assert.Contains(UiText.TopTalkersTitle, html, StringComparison.Ordinal);
        Assert.Contains("192.0.2.50", html, StringComparison.Ordinal);
        Assert.Contains("9,000", html, StringComparison.Ordinal);
        Assert.Contains("/search?source=192.0.2.50", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_WithSourcesAndOutage_ShowsSilenceListAndOutageOverlay()
    {
        // ダッシュボードの時間軸は「現在時刻から直近 1 時間」のため、この窓に入るよう
        // 現在時刻を 1 回だけ読んで相対時刻で構築する（conventions.md の時間窓の扱い）。
        var now = DateTimeOffset.UtcNow;
        var store = new FakeLogStore
        {
            Summaries =
            [
                CreateSummary(1, now.AddMinutes(-10), "192.0.2.1", "hello-1"),
                CreateSummary(2, now.AddMinutes(-5), "192.0.2.2", "hello-2"),
            ],
            Sources =
            [
                // provider 契約どおり最終受信の古い順（無音の疑いが強い順。UI-4）
                new SourceActivity("192.0.2.9", now.AddHours(-30), 3),
                new SourceActivity("192.0.2.1", now.AddMinutes(-10), 42),
            ],
            Events =
            [
                new SystemEvent(SystemEventKinds.DowntimeNormalStop,
                    now.AddMinutes(-30), now.AddMinutes(-25), Approximate: false, Id: 1),
            ],
            Statistics = new LogStoreStatistics(RecordCount: 45, DatabaseSizeBytes: 4096),
        };
        var reader = new FakeStatusReader { RetentionDays = 30 };

        var html = await RenderPageAsync<Dashboard>(store, reader);

        // 送信元別の受信状況（最終受信時刻の古い順の一覧 + 無音時間列。UI-4）
        Assert.Contains(UiText.SourcesTitle, html, StringComparison.Ordinal);
        Assert.Contains("192.0.2.9", html, StringComparison.Ordinal);
        Assert.Contains(UiText.SourceColumnSilence, html, StringComparison.Ordinal);

        // 受信断区間の時間軸への重ね描き（architecture.md §4.4）+ 凡例文言
        Assert.Contains("yagura-timeline-outage", html, StringComparison.Ordinal);
        Assert.Contains(UiText.MissingDataOutage, html, StringComparison.Ordinal);

        // 受信量の推移（棒）
        Assert.Contains("yagura-timeline-bar", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_RetentionDisabled_ShowsDisabledNotice()
    {
        var store = new FakeLogStore();
        var reader = new FakeStatusReader { RetentionDays = null };

        var html = await RenderPageAsync<Dashboard>(store, reader);

        // 不正値フォールバック（削除しない。database.md §3）でも削除の扱いを常時明示する
        Assert.Contains(UiText.RetentionDisabledNotice, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_WithLoss_ShowsLossCardSupplement()
    {
        // 取りこぼしがある場合、累計値だけでなく「サーバ起動からの累計」であることと
        // 開いてからの増分を補足で示す（2026-07-06 試用フィードバック——累計と保存件数が
        // 並ぶと「大半を捨てている」ように読める誤解の緩和）。
        var store = new FakeLogStore();
        var reader = new FakeStatusReader
        {
            RetentionDays = 30,
            Counters =
            [
                new YaguraCounterReading("yagura.ingestion.internal_buffer.dropped", 37_529, IsLoss: true),
                new YaguraCounterReading("yagura.ingestion.spool.evacuated", 130, IsLoss: false),
            ],
        };

        var html = await RenderPageAsync<Dashboard>(store, reader);

        // 累計値そのものは大表示
        Assert.Contains("37,529", html, StringComparison.Ordinal);
        // 「累計であること」を補足で明示（進行中か過去かの手がかり）
        Assert.Contains("サーバ起動からの累計", html, StringComparison.Ordinal);
        // 単一描画では基準 = 現在値のため増分は 0（＝今は増えていない、の読み）
        Assert.Contains("この画面を開いてからは +0 件", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_NoLoss_OmitsLossCardSupplement()
    {
        // 取りこぼし 0 のときはカードを静かに保つ（補足を付けない）。
        var store = new FakeLogStore();
        var reader = new FakeStatusReader
        {
            RetentionDays = 30,
            Counters = [new YaguraCounterReading("yagura.ingestion.internal_buffer.dropped", 0, IsLoss: true)],
        };

        var html = await RenderPageAsync<Dashboard>(store, reader);

        Assert.DoesNotContain("サーバ起動からの累計", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_SpoolEvacuationOngoing_ShowsWarningNoticeAndOngoingSupplement()
    {
        // 一時保管への退避が現在も進行中（SystemStatusReader が消化未完了と判定。Issue #132）:
        // 常時表示の警告通知（ui.md §5.4）+ 累計カードの「進行中」補足の両方が出る。
        var store = new FakeLogStore();
        var reader = new FakeStatusReader
        {
            RetentionDays = 30,
            Counters =
            [
                new YaguraCounterReading("yagura.ingestion.internal_buffer.dropped", 0, IsLoss: true),
                new YaguraCounterReading("yagura.ingestion.spool.evacuated", 42, IsLoss: false),
            ],
            Health = new YaguraHealthReading(
                YaguraHealthKind.Warning,
                [YaguraHealthReason.SpoolEvacuationObserved]),
        };

        var html = await RenderPageAsync<Dashboard>(store, reader);

        Assert.Contains(UiText.SpoolEvacuationNotice, html, StringComparison.Ordinal);
        Assert.Contains(UiText.StatSpoolEvacuatedOngoingSupplement, html, StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.StatSpoolEvacuatedResolvedSupplement, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_SpoolEvacuationResolved_HidesWarningNotice_ShowsResolvedSupplement()
    {
        // 消化完了（スプール使用量が 0 に戻った。Issue #132）後: 常時表示の警告通知は消え、
        // 累計カードは「退避分は格納済み」の補足に切り替わる——累計値そのものはリセットしない
        // （過去に退避があった事実は残す。issue の (B) 論点）。
        var store = new FakeLogStore();
        var reader = new FakeStatusReader
        {
            RetentionDays = 30,
            Counters =
            [
                new YaguraCounterReading("yagura.ingestion.internal_buffer.dropped", 0, IsLoss: true),
                new YaguraCounterReading("yagura.ingestion.spool.evacuated", 42, IsLoss: false),
            ],
            Health = YaguraHealthReading.Ok,
        };

        var html = await RenderPageAsync<Dashboard>(store, reader);

        Assert.DoesNotContain(UiText.SpoolEvacuationNotice, html, StringComparison.Ordinal);
        Assert.Contains(UiText.StatSpoolEvacuatedResolvedSupplement, html, StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.StatSpoolEvacuatedOngoingSupplement, html, StringComparison.Ordinal);
        // 累計値そのものは消えていない(監査上の価値を残す)。
        Assert.Contains("42", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_SpoolNeverEvacuated_OmitsSpoolEvacuatedSupplement()
    {
        // 一度も退避が発生していない（累計 0）場合はカードを静かに保つ（補足を付けない）。
        var store = new FakeLogStore();
        var reader = new FakeStatusReader
        {
            RetentionDays = 30,
            Counters = [new YaguraCounterReading("yagura.ingestion.spool.evacuated", 0, IsLoss: false)],
            Health = YaguraHealthReading.Ok,
        };

        var html = await RenderPageAsync<Dashboard>(store, reader);

        Assert.DoesNotContain(UiText.StatSpoolEvacuatedOngoingSupplement, html, StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.StatSpoolEvacuatedResolvedSupplement, html, StringComparison.Ordinal);
    }

    // ---- ログ検索（ui.md §4・§5.3。architecture.md §4.4・§6） ----

    [Fact]
    public async Task LogSearch_DefaultQuery_ShowsResultsAndRetentionHorizonAndOutages()
    {
        var store = new FakeLogStore
        {
            Summaries =
            [
                CreateSummary(1, Baseline.AddMinutes(-10), "192.0.2.1", "search-hit-1"),
                CreateSummary(2, Baseline.AddMinutes(-5), "192.0.2.2", "search-hit-2"),
            ],
            Events =
            [
                new SystemEvent(SystemEventKinds.DowntimeCrashApproximate,
                    Baseline.AddMinutes(-8), Baseline.AddMinutes(-7), Approximate: true, Id: 1),
            ],
        };
        var reader = new FakeStatusReader { RetentionDays = 30 };

        var html = await RenderPageAsync<LogSearch>(store, reader);

        // 絞り込み強制なし——初期表示で条件なし検索が実行され、結果が並ぶ
        Assert.Contains("search-hit-1", html, StringComparison.Ordinal);
        Assert.Contains("search-hit-2", html, StringComparison.Ordinal);

        // 検索範囲が保持地平より古い（下限なし）ため、保持地平を明示する（database.md §2.3）
        Assert.Contains(UiText.MissingDataRetentionHorizon, html, StringComparison.Ordinal);

        // 受信断区間: 時間軸の帯 + 近似断点の注記（architecture.md §4.4・ui.md §5.3）
        Assert.Contains("yagura-timeline-outage-approximate", html, StringComparison.Ordinal);
        Assert.Contains(UiText.MissingDataOutageApproximateNote, html, StringComparison.Ordinal);

        // 検索条件フォーム（重大度の選択肢・検索ボタン）
        Assert.Contains(UiText.SearchFieldSeverity, html, StringComparison.Ordinal);
        Assert.Contains(UiText.SelectNoneOption, html, StringComparison.Ordinal);
        Assert.Contains(UiText.SearchButton, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogSearch_NoLogsAtAll_ShowsFirstRunEmptyState()
    {
        var store = new FakeLogStore();
        var reader = new FakeStatusReader
        {
            RetentionDays = 30,
            Listeners = [new YaguraListenerEndpoint("UDP", 514)],
        };

        var html = await RenderPageAsync<LogSearch>(store, reader);

        // 条件なしで 0 件 = ログ未着——受信先の案内つき空状態（30 分動線の続き）
        Assert.Contains(UiText.NoLogsEmptyTitle, html, StringComparison.Ordinal);
        Assert.Contains("UDP 受信ポート", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogSearch_ParseFailedRecord_ShowsPlainLanguageLabel()
    {
        var store = new FakeLogStore
        {
            Summaries =
            [
                CreateSummary(1, Baseline.AddMinutes(-1), "192.0.2.1", message: null, parseStatus: ParseStatus.ParseFailed),
            ],
        };
        var reader = new FakeStatusReader { RetentionDays = 30 };

        var html = await RenderPageAsync<LogSearch>(store, reader);

        // 用語対応表（ui.md §7.2）: 解析失敗（raw 保存） → 平易語
        Assert.Contains(UiText.ParseFailedLabel, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogSearch_RecordWithStructuredData_ShowsVerbatimPrefixBeforeMessage()
    {
        // RFC 5424 構造化データを本文の前に原文のまま接頭表示する（オーナー承認済みデザイン。
        // database.md §2.1・ui.md §4——保存・解析は変更せず一覧射影 + 表示層のみの変更）。
        var store = new FakeLogStore
        {
            Summaries =
            [
                CreateSummary(
                    1,
                    Baseline.AddMinutes(-1),
                    "192.0.2.1",
                    "sd-message",
                    structuredData: "[winevt Channel=\"System\" EventID=\"7036\"]"),
            ],
        };
        var reader = new FakeStatusReader { RetentionDays = 30 };

        var html = await RenderPageAsync<LogSearch>(store, reader);

        Assert.Contains("[winevt Channel=\"System\" EventID=\"7036\"]", html, StringComparison.Ordinal);
        Assert.Contains("yagura-sd-prefix", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogSearch_RecordWithoutStructuredData_OmitsPrefix()
    {
        // RFC 3164 送信元等、構造化データを持たないレコードでは接頭表示自体を出さない。
        var store = new FakeLogStore
        {
            Summaries =
            [
                CreateSummary(1, Baseline.AddMinutes(-1), "192.0.2.1", "plain-message", structuredData: null),
            ],
        };
        var reader = new FakeStatusReader { RetentionDays = 30 };

        var html = await RenderPageAsync<LogSearch>(store, reader);

        Assert.Contains("plain-message", html, StringComparison.Ordinal);
        Assert.DoesNotContain("yagura-sd-prefix", html, StringComparison.Ordinal);
    }

    // ---- システム状態（ui.md §4。OS ゲージ注記 = 本 PR の設計判断） ----

    [Fact]
    public async Task SystemStatus_ShowsCountersWithInstrumentIdsAndOsGaugeExplanation()
    {
        var store = new FakeLogStore
        {
            Statistics = new LogStoreStatistics(RecordCount: 123, DatabaseSizeBytes: 2048),
        };
        var reader = new FakeStatusReader
        {
            Counters =
            [
                new YaguraCounterReading("yagura.ingestion.internal_buffer.dropped", 7, IsLoss: true),
                new YaguraCounterReading("yagura.ingestion.spool.evacuated", 3, IsLoss: false),
            ],
        };

        var html = await RenderPageAsync<SystemStatus>(store, reader);

        // カウンタは平易語 + 識別子（開発用語側のキー）の併記（ui.md §4 状態画面の責務）
        Assert.Contains(UiText.CounterInternalBufferDropped, html, StringComparison.Ordinal);
        Assert.Contains("yagura.ingestion.internal_buffer.dropped", html, StringComparison.Ordinal);
        Assert.Contains(UiText.CounterSpoolEvacuated, html, StringComparison.Ordinal);

        // OS 受信破棄ゲージ: 値は表示せず、常時可視の説明を掲示する（M8-3 の設計判断。
        // architecture.md §4.2・D-6——値 0 = 取りこぼしゼロの誤解を生まない側に倒す）
        Assert.Contains(UiText.OsUdpGaugeExplanation, html, StringComparison.Ordinal);
        Assert.Contains(UiText.OsUdpGaugeExplanationSupplement, html, StringComparison.Ordinal);
        Assert.DoesNotContain("yagura.os.udp", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemStatus_ShowsOutageAndEventHistories()
    {
        var store = new FakeLogStore
        {
            Events =
            [
                new SystemEvent(SystemEventKinds.DowntimeCrashApproximate,
                    Baseline.AddHours(-2), Baseline.AddHours(-1), Approximate: true, Id: 1),
                new SystemEvent(SystemEventKinds.RetentionDelete,
                    Baseline.AddMinutes(-30), Baseline.AddMinutes(-29), Approximate: false, Id: 2, Details: "deleted=100"),
            ],
        };
        var reader = new FakeStatusReader();

        var html = await RenderPageAsync<SystemStatus>(store, reader);

        // 受信断履歴（クラッシュ近似はその旨を種別で明示）と動作記録（保持期間削除）が分かれて出る
        Assert.Contains(UiText.OutageHistoryTitle, html, StringComparison.Ordinal);
        Assert.Contains(UiText.OutageKindCrashApproximate, html, StringComparison.Ordinal);
        Assert.Contains(UiText.EventHistoryTitle, html, StringComparison.Ordinal);
        Assert.Contains(UiText.EventKindRetentionDelete, html, StringComparison.Ordinal);
        Assert.Contains("deleted=100", html, StringComparison.Ordinal);

        // 能動通知の記録先の案内（architecture.md §4.6）
        Assert.Contains(UiText.EventLogNote, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemStatus_SpoolDegraded_ShowsDegradedNotice()
    {
        var store = new FakeLogStore();
        var reader = new FakeStatusReader { Spool = null, SpoolDegraded = true };

        var html = await RenderPageAsync<SystemStatus>(store, reader);

        // スプールなし縮退の可視化（architecture.md §1.2「黙って opt-out 相当に落ちることを許さない」）
        Assert.Contains(UiText.HealthReasonSpoolDegraded, html, StringComparison.Ordinal);
        Assert.Contains(UiText.StatSpoolUnavailable, html, StringComparison.Ordinal);
    }

    // ---- 共通骨格（MainLayout。ui.md §4: アプリバー + 左ナビ + 状態帯） ----

    [Fact]
    public async Task MainLayout_ShowsNavigationAndStatusBand()
    {
        var store = new FakeLogStore
        {
            Summaries = [CreateSummary(1, Baseline.AddMinutes(-3), "192.0.2.1", "latest")],
        };
        var reader = new FakeStatusReader();

        var html = await RenderPageAsync<MainLayout>(store, reader);

        // 左ナビゲーション（画面一覧。ui.md §4）
        Assert.Contains(UiText.NavDashboard, html, StringComparison.Ordinal);
        Assert.Contains(UiText.NavSearch, html, StringComparison.Ordinal);
        Assert.Contains(UiText.NavStatus, html, StringComparison.Ordinal);
        Assert.Contains("href=\"/search\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/status\"", html, StringComparison.Ordinal);

        // 全画面共通の状態帯: 正常の既定文言 + 最終受信時刻の併記 + 送信元別への導線（ui.md §5.1）
        Assert.Contains(UiText.StatusBandOkTitle, html, StringComparison.Ordinal);
        Assert.Contains(UiText.StatusBandOkSummary, html, StringComparison.Ordinal);
        Assert.Contains(UiText.StatusBandLastReceivedLabel, html, StringComparison.Ordinal);
        Assert.Contains(UiText.StatusBandSourcesLinkText, html, StringComparison.Ordinal);

        // 閲覧リスナ帰属（既定 = IsAdminListener 未設定）では管理リンクを出さない（安全側。M8-4）。
        Assert.DoesNotContain("href=\"/admin/setup\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/admin/promotion\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/admin/circuits\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainLayout_AdminListener_ShowsAdminLinks()
    {
        // 管理リスナ帰属（IsAdminListener == true）の circuit では設定・昇格・接続管理への
        // 導線を出す（2026-07-06 試用フィードバック「8515 で開いても導線が無い」への対応）。
        var store = new FakeLogStore();
        var reader = new FakeStatusReader();
        var adminContext = new Yagura.Web.Circuits.YaguraCircuitContext { IsAdminListener = true };

        var html = await CommonComponentRenderHarness.RenderAsync<MainLayout>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ILogStore>(store);
                services.AddSingleton<IYaguraSystemStatusReader>(reader);
                services.AddScoped(_ => adminContext);
            },
            includePopoverProvider: false);

        Assert.Contains("href=\"/admin/setup\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/promotion\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/circuits\"", html, StringComparison.Ordinal);
        Assert.Contains(UiText.AdminPromotionWizardTitle, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainLayout_WarningHealth_ShowsReasonSummary()
    {
        var store = new FakeLogStore();
        var reader = new FakeStatusReader
        {
            Health = new YaguraHealthReading(
                YaguraHealthKind.Warning,
                [YaguraHealthReason.SpoolEvacuationObserved]),
        };

        var html = await RenderPageAsync<MainLayout>(store, reader);

        Assert.Contains(UiText.StatusBandWarningTitle, html, StringComparison.Ordinal);
        Assert.Contains(UiText.HealthReasonSpoolEvacuation, html, StringComparison.Ordinal);
    }

    // ---- ハーネス ----

    private static Task<string> RenderPageAsync<TComponent>(FakeLogStore store, FakeStatusReader reader)
        where TComponent : Microsoft.AspNetCore.Components.IComponent =>
        CommonComponentRenderHarness.RenderAsync<TComponent>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ILogStore>(store);
                services.AddSingleton<IYaguraSystemStatusReader>(reader);
                // MainLayout は管理リンクの出し分けに circuit のリスナ帰属を読む（M8-4）。
                // 既定インスタンス（IsAdminListener 未設定＝閲覧相当）を注入する——管理リンクが
                // 出ない側の描画になる（管理リスナ帰属の分岐は別テストで固定）。
                services.AddScoped<Yagura.Web.Circuits.YaguraCircuitContext>();
            },
            // MainLayout はプロバイダ群（MudPopoverProvider 等）を自身が内包するため、
            // ハーネス側のプロバイダ同居を外す（二重登録はエラー）。
            includePopoverProvider: typeof(TComponent) != typeof(MainLayout));

    private static LogRecordSummary CreateSummary(
        long id,
        DateTimeOffset receivedAt,
        string sourceAddress,
        string? message,
        ParseStatus parseStatus = ParseStatus.Parsed,
        string? structuredData = null) =>
        new(
            Id: id,
            ReceivedAt: receivedAt,
            SourceAddress: sourceAddress,
            SourcePort: 514,
            Protocol: Protocol.Udp,
            ParseStatus: parseStatus,
            DeviceTimestamp: null,
            Facility: 1,
            Severity: 5,
            Hostname: "host",
            AppName: "app",
            ProcId: null,
            MsgId: null,
            StructuredData: structuredData,
            Message: message);

    /// <summary>閲覧画面が使う読み取り口のフェイク（データはテストごとにシードする）。</summary>
    private sealed class FakeLogStore : ILogStore
    {
        public List<LogRecordSummary> Summaries { get; init; } = [];
        public List<SystemEvent> Events { get; init; } = [];
        public List<SourceActivity> Sources { get; init; } = [];
        public LogRecord? Record { get; init; }
        public LogStoreStatistics Statistics { get; init; } = new(RecordCount: 0, DatabaseSizeBytes: 0);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("閲覧画面は書き込みを行わない（L-5）。");

        public Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<LogRecordSummary>)Summaries
                .OrderByDescending(s => s.ReceivedAt).Take(limit).ToList());

        public Task<IReadOnlyList<LogRecordSummary>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<LogRecordSummary>)Summaries
                .Where(s => query.ReceivedAtFrom is not { } from || s.ReceivedAt >= from)
                .Where(s => query.ReceivedAtTo is not { } to || s.ReceivedAt <= to)
                .OrderByDescending(s => s.ReceivedAt)
                .Take(query.Limit)
                .ToList());

        public Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("閲覧画面は書き込みを行わない（L-5）。");

        public Task<DeleteOlderThanResult> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("閲覧画面は書き込みを行わない（L-5）。");

        public Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Statistics);

        public Task<LogRecord?> FindByIdAsync(long id, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(Record);

        public Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, TimeSpan timeout, string? kind = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<SystemEvent>)Events
                .Where(e => from is not { } f || e.EndAt >= f)
                .Where(e => to is not { } t || e.StartAt <= t)
                .OrderByDescending(e => e.StartAt)
                .Take(limit)
                .ToList());

        public Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<SourceActivity>)Sources
                .OrderBy(s => s.LastReceivedAt).Take(limit).ToList());

        public List<SeverityCount> SeverityDistribution { get; init; } = [];
        public List<SourceActivity> TopTalkers { get; init; } = [];

        public Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<SeverityCount>)SeverityDistribution);

        public Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(DateTimeOffset from, DateTimeOffset to, int limit, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<SourceActivity>)TopTalkers.OrderByDescending(t => t.RecordCount).Take(limit).ToList());
    }

    private sealed class FakeStatusReader : IYaguraSystemStatusReader
    {
        public List<YaguraCounterReading> Counters { get; init; } =
        [
            new("yagura.ingestion.internal_buffer.dropped", 0, IsLoss: true),
            new("yagura.ingestion.spool.evacuated", 0, IsLoss: false),
        ];

        public YaguraSpoolReading? Spool { get; init; } = new(CurrentUsageBytes: 1024, QuotaBytes: 1024 * 1024);

        public bool SpoolDegraded { get; init; }

        public YaguraHealthReading Health { get; init; } = YaguraHealthReading.Ok;

        public int? RetentionDays { get; init; } = 30;

        public List<YaguraListenerEndpoint> Listeners { get; init; } =
            [new YaguraListenerEndpoint("UDP", 514)];

        public YaguraSystemStatusSnapshot ReadCurrent() => new(
            TakenAt: DateTimeOffset.UtcNow,
            Counters: Counters,
            Spool: Spool,
            SpoolDegraded: SpoolDegraded,
            Health: Health,
            RetentionDays: RetentionDays,
            Listeners: Listeners);
    }
}
