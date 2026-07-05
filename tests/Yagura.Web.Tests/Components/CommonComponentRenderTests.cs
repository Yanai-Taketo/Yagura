using Microsoft.AspNetCore.Components;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 共通コンポーネント初期セット（ui.md §3.1・§5。M8-2。Issue #69）の表示確認:
/// 実描画したマークアップに対して、各部品の規約（文言・構造・aria 属性）を検証する。
/// 文言は UiText（§7 用語対応表の実装位置）を参照して突合する——リテラルの二重管理をしない。
/// </summary>
public sealed class CommonComponentRenderTests
{
    private static readonly TimeZoneInfo TestTimeZone =
        TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");

    // ---- 状態帯（ui.md §3.1 状態帯・§5.1） ----

    [Fact]
    public async Task StatusBand_Ok_ShowsTitleSummaryAndLastReceived()
    {
        var lastReceived = new DateTimeOffset(2026, 7, 4, 12, 5, 11, TimeSpan.Zero);
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraStatusBand>(new()
        {
            [nameof(YaguraStatusBand.Status)] = YaguraStatusKind.Ok,
            [nameof(YaguraStatusBand.LastReceivedAt)] = lastReceived,
            [nameof(YaguraStatusBand.SourcesLinkHref)] = "/sources",
            [nameof(YaguraStatusBand.TimeZone)] = TestTimeZone,
        });

        // 3 状態の見出し + 既定サマリ（ui.md §5.1 の確定文言——観測できる範囲に限る言い切り）
        Assert.Contains(UiText.StatusBandOkTitle, html, StringComparison.Ordinal);
        Assert.Contains(UiText.StatusBandOkSummary, html, StringComparison.Ordinal);

        // 全送信元合算の最終受信時刻の併記 + 送信元別への導線（ui.md §5.1）
        Assert.Contains("2026-07-04 21:05:11 (UTC+09:00)", html, StringComparison.Ordinal);
        Assert.Contains(UiText.StatusBandSourcesLinkText, html, StringComparison.Ordinal);
        Assert.Contains("href=\"/sources\"", html, StringComparison.Ordinal);

        // 色だけで意味を伝えない（ui.md §2.1・§8）: アイコン（svg）と文言の両方を伴う
        Assert.Contains("<svg", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(YaguraStatusKind.Warning)]
    [InlineData(YaguraStatusKind.Error)]
    public async Task StatusBand_WarningAndError_ShowTitles(YaguraStatusKind status)
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraStatusBand>(new()
        {
            [nameof(YaguraStatusBand.Status)] = status,
            [nameof(YaguraStatusBand.Summary)] = "テスト用の事象サマリ",
        });

        var expectedTitle = status == YaguraStatusKind.Warning
            ? UiText.StatusBandWarningTitle
            : UiText.StatusBandErrorTitle;
        Assert.Contains(expectedTitle, html, StringComparison.Ordinal);
        Assert.Contains("テスト用の事象サマリ", html, StringComparison.Ordinal);
    }

    // ---- 時刻表示（ui.md §6） ----

    [Fact]
    public async Task Timestamp_RendersLocalizedTextAndMachineReadableUtc()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraTimestamp>(new()
        {
            [nameof(YaguraTimestamp.Value)] = new DateTimeOffset(2026, 7, 4, 12, 5, 11, TimeSpan.Zero),
            [nameof(YaguraTimestamp.TimeZone)] = TestTimeZone,
        });

        // 表示はローカル化 + オフセット明示（ui.md §6 の契約）、datetime 属性は機械可読の UTC
        Assert.Contains("2026-07-04 21:05:11 (UTC+09:00)", html, StringComparison.Ordinal);
        Assert.Contains("datetime=\"2026-07-04T12:05:11.0000000Z\"", html, StringComparison.Ordinal);
    }

    // ---- 空状態（ui.md §3.1） ----

    [Fact]
    public async Task EmptyState_ShowsTitleAndNextAction()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraEmptyState>(new()
        {
            [nameof(YaguraEmptyState.Title)] = "まだログがありません",
            [nameof(YaguraEmptyState.NextAction)] = CommonComponentRenderHarness.Text("機器の送信先をこのサーバに設定してください"),
        });

        Assert.Contains("まだログがありません", html, StringComparison.Ordinal);
        // 次に取るべき行動を必ず示す（ui.md §3.1）
        Assert.Contains("機器の送信先をこのサーバに設定してください", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyState_WithoutNextAction_Throws()
    {
        // 無地の空状態（次の行動なし）は構造上作れない（ui.md §3.1 の強制）
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CommonComponentRenderHarness.RenderAsync<YaguraEmptyState>(new()
            {
                [nameof(YaguraEmptyState.Title)] = "まだログがありません",
            }));
    }

    // ---- コピー可能フィールド（ui.md §3.1 空状態の受信先表示） ----

    [Fact]
    public async Task CopyField_ShowsMonospaceValueAndAccessibleCopyButton()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraCopyField>(new()
        {
            [nameof(YaguraCopyField.Label)] = "受信先",
            [nameof(YaguraCopyField.Value)] = "192.168.1.10:514 (UDP)",
        });

        Assert.Contains("受信先", html, StringComparison.Ordinal);
        Assert.Contains("192.168.1.10:514 (UDP)", html, StringComparison.Ordinal);
        // 等幅表示（ui.md §2.2 の等幅スタックは共通コンポーネント実装内部で適用）
        Assert.Contains("yagura-monospace", html, StringComparison.Ordinal);
        // コピーボタンは読み上げ可能なラベルを持つ（ui.md §8）
        Assert.Contains("aria-label=\"受信先をコピー\"", html, StringComparison.Ordinal);
    }

    // ---- フォーム（ui.md §3.1） ----

    [Fact]
    public async Task TextField_LabelAboveInput_RequiredMark_ErrorBelow()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraTextField>(new()
        {
            [nameof(YaguraTextField.Label)] = "保存先の名前",
            [nameof(YaguraTextField.Required)] = true,
            [nameof(YaguraTextField.ErrorText)] = "保存先の名前を入力してください",
        });

        // ラベルは入力の上（ui.md §3.1）: label 要素が input より先に現れる
        var labelIndex = html.IndexOf("保存先の名前", StringComparison.Ordinal);
        var inputIndex = html.IndexOf("<input", StringComparison.Ordinal);
        Assert.True(labelIndex >= 0 && inputIndex >= 0 && labelIndex < inputIndex,
            "ラベルが入力要素より前に描画されていない（ui.md §3.1 フォーム規約）。");

        // 必須は「必須」表記（記号 * だけにしない。ui.md §3.1）
        Assert.Contains(UiText.FormRequiredMark, html, StringComparison.Ordinal);

        // 検証エラーは項目直下に文言で表示（色だけにしない）——エラー文言が input の後に現れる
        var errorIndex = html.IndexOf("保存先の名前を入力してください", StringComparison.Ordinal);
        Assert.True(errorIndex > inputIndex, "エラー文言が入力要素の下に描画されていない（ui.md §3.1）。");
        Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);
    }

    // ---- テーブル（ui.md §3.1） ----

    [Fact]
    public async Task Table_RendersRows_PagerAndTruncationNotice()
    {
        var items = new[] { "行A", "行B", "行C" };
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraTable<string>>(new()
        {
            [nameof(YaguraTable<string>.Items)] = items,
            [nameof(YaguraTable<string>.HeaderContent)] = CommonComponentRenderHarness.Text("メッセージ"),
            [nameof(YaguraTable<string>.RowTemplate)] = (RenderFragment<string>)(item =>
                builder => builder.AddContent(0, item)),
            [nameof(YaguraTable<string>.TruncatedAtLimit)] = true,
            [nameof(YaguraTable<string>.OnRowSelected)] =
                EventCallback.Factory.Create<string>(new object(), _ => { }),
        });

        Assert.Contains("行A", html, StringComparison.Ordinal);
        Assert.Contains("行C", html, StringComparison.Ordinal);

        // ページング必須（結果上限件数の一括レンダリングを行わない——ui.md §3.1）
        Assert.Contains(UiText.TableRowsPerPage, html, StringComparison.Ordinal);

        // 打ち切りは表の末尾に件数と共に明示（ui.md §3.1・§5.3）
        var truncatedText = string.Format(
            System.Globalization.CultureInfo.InvariantCulture, UiText.MissingDataTruncatedFormat, items.Length);
        Assert.Contains(truncatedText, html, StringComparison.Ordinal);

        // 詳細への到達はキーボードでも可能（ui.md §8）: フォーカス可能な詳細ボタン列が付く
        Assert.Contains($"aria-label=\"{UiText.TableRowDetailLabel}\"", html, StringComparison.Ordinal);
    }

    // ---- 欠けているデータの明示（ui.md §5.3） ----

    [Fact]
    public async Task MissingDataNotice_RetentionHorizon_ShowsFixedWording()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraMissingDataNotice>(new()
        {
            [nameof(YaguraMissingDataNotice.Kind)] = YaguraMissingDataKind.RetentionHorizon,
        });

        Assert.Contains(UiText.MissingDataRetentionHorizon, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingDataNotice_ApproximateOutage_MarksApproximation()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraMissingDataNotice>(new()
        {
            [nameof(YaguraMissingDataNotice.Kind)] = YaguraMissingDataKind.ReceptionOutageApproximate,
            [nameof(YaguraMissingDataNotice.PeriodStart)] = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero),
            [nameof(YaguraMissingDataNotice.PeriodEnd)] = new DateTimeOffset(2026, 7, 4, 1, 0, 0, TimeSpan.Zero),
            [nameof(YaguraMissingDataNotice.TimeZone)] = TestTimeZone,
        });

        // 用語対応表: 受信断 → 受信できなかった時間帯（ui.md §7.2）
        Assert.Contains(UiText.MissingDataOutage, html, StringComparison.Ordinal);
        // クラッシュ由来の近似断点はその旨を印す（ui.md §5.3）
        Assert.Contains(UiText.MissingDataOutageApproximateNote, html, StringComparison.Ordinal);
        // 区間は時刻契約（ui.md §6）で表示される
        Assert.Contains("2026-07-04 09:00:00 (UTC+09:00)", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetentionNotice_ShowsCurrentValue()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraRetentionNotice>(new()
        {
            [nameof(YaguraRetentionNotice.RetentionDays)] = 30,
        });

        // 保持期間の常時明示（ui.md §5.3 の確定文言 + 現在値）
        Assert.Contains("30 日より古いログは自動的に削除されます", html, StringComparison.Ordinal);
    }

    // ---- 設定・システム由来の警告と案内（ui.md §5.4） ----

    [Fact]
    public async Task SystemNotice_Warning_ShowsMessageWithIcon()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraSystemNotice>(new()
        {
            [nameof(YaguraSystemNotice.Severity)] = YaguraNoticeSeverity.Warning,
            [nameof(YaguraSystemNotice.Message)] = UiText.SpoolEvacuationNotice,
        });

        // 一時保管への退避は色 + アイコン + 補足文言のセット（ui.md §5.4）
        Assert.Contains(UiText.SpoolEvacuationNotice, html, StringComparison.Ordinal);
        Assert.Contains("<svg", html, StringComparison.Ordinal);
        Assert.Contains("mud-alert", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemNotice_Info_ShowsPromotionSuggestionWithoutActions()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraSystemNotice>(new()
        {
            [nameof(YaguraSystemNotice.Severity)] = YaguraNoticeSeverity.Info,
            [nameof(YaguraSystemNotice.Message)] = UiText.PromotionSuggestionViewer,
        });

        Assert.Contains("切り替えと案内の抑制はサーバ上の設定画面から行えます", html, StringComparison.Ordinal);
        // 閲覧画面の通知は操作を含めない（ui.md §5.4）——ボタン要素を持たない
        Assert.DoesNotContain("<button", html, StringComparison.Ordinal);
    }

    // ---- 最終更新時刻とステール警告（ui.md §5.2） ----

    [Fact]
    public async Task StaleGuard_RendersLastUpdatedAndHiddenOverlay()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraStaleGuard>(new()
        {
            [nameof(YaguraStaleGuard.UpdateInterval)] = TimeSpan.FromSeconds(5),
        });

        // 最終更新時刻の常時表示（ui.md §5.2）
        Assert.Contains(UiText.LastUpdatedLabel, html, StringComparison.Ordinal);

        // 警告オーバーレイは初期状態では隠れている（表示はクライアント側 JS が自律制御する）
        Assert.Contains("yagura-stale-overlay", html, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\"", html, StringComparison.Ordinal);

        // 文言は受信への影響を必ず含める（ui.md §5.2 の確定文言）
        Assert.Contains(UiText.StaleWarningBody, html, StringComparison.Ordinal);
    }

    // ---- ボタン（ui.md §3.1） ----

    [Fact]
    public async Task Button_Primary_RendersFilledPrimary()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraButton>(new()
        {
            [nameof(YaguraButton.Role)] = YaguraButtonRole.Primary,
            [nameof(YaguraButton.ChildContent)] = CommonComponentRenderHarness.Text("保存する"),
        });

        Assert.Contains("保存する", html, StringComparison.Ordinal);
        Assert.Contains("mud-button-filled-primary", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Button_Destructive_RendersErrorColor()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraButton>(new()
        {
            [nameof(YaguraButton.Role)] = YaguraButtonRole.Destructive,
            [nameof(YaguraButton.ChildContent)] = CommonComponentRenderHarness.Text("削除する"),
            [nameof(YaguraButton.ConfirmTitle)] = "ログの削除",
            [nameof(YaguraButton.ConfirmSummary)] = "選択したログを削除します。この操作は取り消せません。",
            [nameof(YaguraButton.ConfirmActionLabel)] = "削除する",
        });

        // 破壊的操作は state-error 系（ui.md §3.1）
        Assert.Contains("mud-button-filled-error", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Button_Destructive_WithoutConfirmParameters_Throws()
    {
        // 確認ダイアログ必須（ui.md §3.1）を構造で強制: 確認内容なしの破壊的ボタンは作れない
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CommonComponentRenderHarness.RenderAsync<YaguraButton>(new()
            {
                [nameof(YaguraButton.Role)] = YaguraButtonRole.Destructive,
                [nameof(YaguraButton.ChildContent)] = CommonComponentRenderHarness.Text("削除する"),
            }));
    }
}
