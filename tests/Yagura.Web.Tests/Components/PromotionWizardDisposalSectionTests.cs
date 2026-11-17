using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;
using Yagura.Web.Circuits;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 昇格ページの後始末セクション（旧・組み込み DB ファイルの処分。database.md §6.3・ui.md §5.4）の表示と操作。
/// </summary>
/// <remarks>
/// 状態表の各状態での表示、照合 → 方式の選択 → 保管先 → 確認ダイアログ → 開始の流れ、確認ダイアログの並び順と
/// 必須の確認を固定する。確認ダイアログを経る経路を通すため <c>MudDialogProvider</c> と同居させて描画する。
/// </remarks>
public sealed class PromotionWizardDisposalSectionTests : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly BunitContext _ctx = new();
    private readonly FakeOldDatabaseDisposalService _disposal = new();
    private readonly ScriptedLogMigrationService _migration = new();

    public PromotionWizardDisposalSectionTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<IPromotionWizardService>(new IdlePromotionWizardService());
        _ctx.Services.AddSingleton<ILogMigrationService>(_migration);
        _ctx.Services.AddSingleton<IOldDatabaseDisposalService>(_disposal);
        _ctx.Services.AddSingleton(new YaguraCircuitContext());
        _ctx.Services.AddSingleton(new YaguraCircuitAuthenticationStateProvider());
        _ctx.Services.AddSingleton<IYaguraNotifier>(new SilentNotifier());
        _ctx.Services.AddSingleton<IConfigurationReloadService>(new RecordingConfigurationReloadService());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    // ---- 状態表の各状態 ----

    [Fact]
    public void NotPromoted_DoesNotShowTheSection()
    {
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.NotPromoted);
        _disposal.Status = FakeOldDatabaseDisposalService.NotApplicable();

        var cut = Render();

        Assert.DoesNotContain(UiText.OldDatabaseDisposalSectionTitle, cut.Markup);
    }

    [Fact]
    public void Pending_ShowsTheSectionBelowTheMigrationSection_WithTargetFilesAndMigrationState()
    {
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.Completed);
        _disposal.Status = FakeOldDatabaseDisposalService.Pending();

        var cut = Render();
        var markup = cut.Markup;

        Assert.Contains(UiText.OldDatabaseDisposalSectionTitle, markup);
        Assert.True(
            markup.IndexOf(UiText.MigrationSectionTitle, StringComparison.Ordinal)
                < markup.IndexOf(UiText.OldDatabaseDisposalSectionTitle, StringComparison.Ordinal),
            "後始末セクションは移行セクションの下に置く。");
        Assert.StartsWith("受信と保存は正常に続いています。", UiText.OldDatabaseDisposalSectionDescription);
        Assert.Contains(UiText.OldDatabaseDisposalSectionDescription, markup);

        // 対象ファイル（パス・サイズ）と合計。
        Assert.Contains(FakeOldDatabaseDisposalService.BodyPath, markup);
        Assert.Contains(FakeOldDatabaseDisposalService.WalPath, markup);
        Assert.Contains(string.Format(UiText.OldDatabaseDisposalTotalSizeFormat, "3 MB"), markup);

        // 移行の状態（完了の表示は件数による判定であり、時間帯の照合は後始末の直前に行うと示す）。
        Assert.Contains(UiText.OldDatabaseDisposalMigrationCompleted, markup);

        // 照合の前は方式の選択も実行ボタンも出さない（確認の材料が揃っていない）。
        Assert.Contains(UiText.OldDatabaseDisposalInspect, markup);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalExecute, markup);
    }

    [Fact]
    public void MigrationRunning_SaysSo_AndOffersNoDisposalOperation()
    {
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.Running);
        _disposal.Status = FakeOldDatabaseDisposalService.MigrationRunning();

        var cut = Render();

        Assert.Contains(UiText.OldDatabaseDisposalMigrationRunning, cut.Markup);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalInspect, cut.Markup);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalExecute, cut.Markup);
    }

    [Fact]
    public void RunningBeforeDeletion_ShowsTheStage_AndCanBeCancelled()
    {
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.DisposalInProgress);
        _disposal.Status = FakeOldDatabaseDisposalService.Running(OldDatabaseDisposalStage.Exporting);

        var cut = Render();

        Assert.Contains(UiText.OldDatabaseDisposalStageExporting, cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalLowTrafficNote, cut.Markup);
        ButtonByText(cut, UiText.OldDatabaseDisposalCancel).Click();

        cut.WaitForAssertion(() => Assert.Contains(UiText.OldDatabaseDisposalCancelAccepted, cut.Markup), WaitTimeout);
        Assert.Equal(1, _disposal.CancelCount);
    }

    [Fact]
    public void RunningInDeletion_ShowsTheStage_AndOffersNoCancel()
    {
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.DisposalInProgress);
        _disposal.Status = FakeOldDatabaseDisposalService.Running(OldDatabaseDisposalStage.Deleting);

        var cut = Render();

        Assert.Contains(UiText.OldDatabaseDisposalStageDeleting, cut.Markup);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalCancel, cut.Markup);
    }

    [Fact]
    public void LastAttemptFailed_ShowsTheResultAndRemainingFiles_AndCanBeRunAgain()
    {
        var failed = new OldDatabaseDisposalResult(
            OldDatabaseDisposalOutcome.DeletePartiallyFailed,
            "元のファイルの一部を削除できませんでした（yagura.db）。閉じてから、もう一度実行してください。",
            OldDatabaseDisposalReason.DeleteFailed,
            OldDatabaseDisposalMethod.Archive,
            ArchiveFilePath: @"D:\Archive\yagura-archive-20261116T010000Z\yagura-archive-20261116T010000Z.db",
            RemainingFiles: [FakeOldDatabaseDisposalService.BodyPath]);
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.Completed);
        _disposal.Status = FakeOldDatabaseDisposalService.LastAttemptFailed(failed);

        var cut = Render();

        Assert.Contains(UiText.OldDatabaseDisposalLastFailedTitle, cut.Markup);
        Assert.Contains(failed.Message, cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalResultRemainingFilesLabel, cut.Markup);
        Assert.Contains(failed.ArchiveFilePath!, cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalInspect, cut.Markup);
    }

    [Fact]
    public void CompletedInThisProcess_ShowsOnlyTheResult_WithTheArchiveFileAndItsHash()
    {
        var completed = new OldDatabaseDisposalResult(
            OldDatabaseDisposalOutcome.Completed,
            "以前の保存先のファイルを書き出して保管し、元のファイルを削除しました。",
            Method: OldDatabaseDisposalMethod.Archive,
            ArchiveFilePath: @"D:\Archive\yagura-archive-20261116T010000Z\yagura-archive-20261116T010000Z.db",
            ArchiveSha256: new string('a', 64));
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.NoSourceDatabase);
        _disposal.Status = FakeOldDatabaseDisposalService.NotApplicable(completed);

        var cut = Render();

        Assert.Contains(UiText.OldDatabaseDisposalCompletedTitle, cut.Markup);
        Assert.Contains(completed.ArchiveFilePath!, cut.Markup);
        Assert.Contains(completed.ArchiveSha256!, cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalResponsibility, cut.Markup);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalInspect, cut.Markup);
    }

    [Fact]
    public void RecordedOldFileUnderAnotherName_ShowsThePathAndManualGuidance()
    {
        const string misplaced = @"C:\ProgramData\Yagura\old-name.db";
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.NoSourceDatabase);
        _disposal.Status = FakeOldDatabaseDisposalService.NotApplicable() with { MisplacedRecordedOldDatabasePath = misplaced };

        var cut = Render();

        Assert.Contains(string.Format(UiText.OldDatabaseDisposalMisplacedFormat, misplaced), cut.Markup);
    }

    // ---- 照合 → 方式の選択 → 確認 → 開始 ----

    [Fact]
    public async Task Inspect_Migrated_OffersBothMethodsWithNoDefault_AndTheGuidance()
    {
        Pending(FakeOldDatabaseDisposalService.SqliteInspection());
        var cut = Render();

        await InspectAsync(cut);

        Assert.Contains(UiText.OldDatabaseDisposalMigrated, cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalMethodGuidance, cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalMethodArchiveDescription, cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalMethodDeleteDescription, cut.Markup);

        // 既定の選択は置かない。
        var select = MethodSelect(cut);
        Assert.Equal(string.Empty, select.GetAttribute("value") ?? string.Empty);
        Assert.Contains(UiText.OldDatabaseDisposalMethodEmptyOption, select.TextContent);
        Assert.True(ButtonByText(cut, UiText.OldDatabaseDisposalExecute).HasAttribute("disabled"));
    }

    [Fact]
    public async Task Delete_Migrated_ConfirmsWithoutTheAcknowledgement_AndStartsWithTheToken()
    {
        Pending(FakeOldDatabaseDisposalService.SqliteInspection());
        var cut = Render();
        await InspectAsync(cut);
        MethodSelect(cut).Change("delete");

        var executing = ClickExecuteAsync(cut);
        var dialog = WaitForDialog(cut);
        var text = dialog.TextContent;

        Assert.Contains(UiText.OldDatabaseDisposalConfirmSummaryDelete, text);
        Assert.Contains(FakeOldDatabaseDisposalService.BodyPath, text);
        Assert.Contains(UiText.OldDatabaseDisposalConfirmBackup, text);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalConfirmWithoutMigration, text);

        await ConfirmAsync(cut);
        await executing;

        var request = Assert.Single(_disposal.StartRequests);
        Assert.Equal(OldDatabaseDisposalMethod.Delete, request.Method);
        Assert.Equal(FakeOldDatabaseDisposalService.Token, request.IdempotencyToken);
        Assert.False(request.ConfirmDisposeWithoutMigration);
        Assert.False(request.ConfirmDeleteUnverifiableFile);
        Assert.Null(request.ArchiveParentDirectory);
    }

    [Fact]
    public async Task Unmigrated_ShowsTheRecordsWrittenWhileSqlServerWasUnavailable_AndTheUnmigratedRecords()
    {
        Pending(FakeOldDatabaseDisposalService.SqliteInspection(FakeOldDatabaseDisposalService.UnmigratedAfterSwitch()));
        var cut = Render();

        await InspectAsync(cut);

        // 縮退運転で書き込まれたログは「古い残骸」と読ませない——件数と期間を添えて区別する（ui.md §5.4）。
        Assert.Contains("SQL Server に保存できなかった間に、組み込みデータベースへ保存したログが 42 件あります", cut.Markup);
        Assert.Contains("SQL Server に無い『受信できなかった時間帯』などの記録が 2 件あります", cut.Markup);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalMigrated, cut.Markup);
    }

    [Fact]
    public async Task TargetCannotReconcile_CountsWithoutAKnownPeriod_AreShownWithoutEmptyParentheses()
    {
        // 移行先へ問い合わせられないとき、システムイベントの期間は分からない——空の括弧で期間を欠いたように見せない。
        var judgment = new OldDatabaseMigrationJudgment(
            false,
            [OldDatabaseMigrationGap.TargetCannotReconcile],
            UnmigratedRecordCount: 1200,
            UnmigratedOldest: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            UnmigratedNewest: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            RecordsAfterSwitchCount: 0,
            RecordsAfterSwitchOldest: null,
            RecordsAfterSwitchNewest: null,
            UnmigratedSystemEventCount: 3,
            UnmigratedSystemEventOldest: null,
            UnmigratedSystemEventNewest: null);
        Pending(FakeOldDatabaseDisposalService.SqliteInspection(judgment));
        var cut = Render();
        await InspectAsync(cut);

        Assert.Contains(UiText.OldDatabaseDisposalGapTargetCannotReconcile, cut.Markup);
        Assert.Contains("記録が 3 件あります。", cut.Markup);
        Assert.DoesNotContain("（）", cut.Markup);

        MethodSelect(cut).Change("delete");
        var executing = ClickExecuteAsync(cut);
        var dialog = WaitForDialog(cut);
        Assert.Contains("記録が 3 件あります。削除すると失われます。", dialog.TextContent);
        Assert.DoesNotContain("（）", dialog.TextContent);
        await ButtonByText(cut, UiText.ConfirmDialogCancel).ClickAsync(new MouseEventArgs());
        await executing;
    }

    [Fact]
    public async Task Unmigrated_Dialog_PutsIrreversibilityAndTheUnmigratedCountsOnTop_AndRequiresTheCheckbox()
    {
        Pending(FakeOldDatabaseDisposalService.SqliteInspection(FakeOldDatabaseDisposalService.UnmigratedAfterSwitch()));
        var cut = Render();
        await InspectAsync(cut);
        MethodSelect(cut).Change("delete");

        var executing = ClickExecuteAsync(cut);
        var dialog = WaitForDialog(cut);
        var text = dialog.TextContent;

        // 並び順: 元に戻せない → 未移行の件数・期間 → 確認のチェックボックス → 方式・対象ファイル。
        var summary = text.IndexOf(UiText.OldDatabaseDisposalConfirmSummaryDelete, StringComparison.Ordinal);
        var unmigrated = text.IndexOf("まだ移行していないログが 42 件あります", StringComparison.Ordinal);
        var systemEvents = text.IndexOf("受信できなかった時間帯』などの記録が 2 件あります", StringComparison.Ordinal);
        var checkbox = text.IndexOf(UiText.OldDatabaseDisposalConfirmWithoutMigration, StringComparison.Ordinal);
        var method = text.IndexOf(UiText.OldDatabaseDisposalConfirmMethodLabel, StringComparison.Ordinal);
        var targets = text.IndexOf(FakeOldDatabaseDisposalService.BodyPath, StringComparison.Ordinal);
        Assert.True(summary >= 0 && unmigrated > summary, text);
        Assert.True(systemEvents > summary, text);
        Assert.True(checkbox > unmigrated && checkbox > systemEvents, text);
        Assert.True(method > checkbox && targets > checkbox, text);
        Assert.Contains("画面から参照できないまま失われます", text);

        // 明示確認は既定で未確認。確認するまで確認ボタンは押せない。
        var acknowledgement = dialog.QuerySelector("input[type=checkbox]")!;
        Assert.False(acknowledgement.HasAttribute("checked"));
        Assert.True(ConfirmButton(cut).HasAttribute("disabled"));

        cut.Find(".mud-dialog input[type=checkbox]").Change(true);
        cut.WaitForAssertion(() => Assert.False(ConfirmButton(cut).HasAttribute("disabled")), WaitTimeout);

        await ConfirmAsync(cut);
        await executing;

        var request = Assert.Single(_disposal.StartRequests);
        Assert.True(request.ConfirmDisposeWithoutMigration);
    }

    [Fact]
    public async Task ConfirmedStart_CarriesTheDigestOfTheInspectionItConfirmed()
    {
        // 明示確認は画面に示した照合の結果に対してのもの——開始時の照合し直しで結果が変わっていればサービスが拒否する。
        Pending(FakeOldDatabaseDisposalService.SqliteInspection(FakeOldDatabaseDisposalService.UnmigratedAfterSwitch())
            with { ConfirmationDigest = "digest-of-the-shown-inspection" });
        var cut = Render();
        await InspectAsync(cut);
        MethodSelect(cut).Change("delete");

        var executing = ClickExecuteAsync(cut);
        WaitForDialog(cut);
        cut.Find(".mud-dialog input[type=checkbox]").Change(true);
        cut.WaitForAssertion(() => Assert.False(ConfirmButton(cut).HasAttribute("disabled")), WaitTimeout);
        await ConfirmAsync(cut);
        await executing;

        var request = Assert.Single(_disposal.StartRequests);
        Assert.Equal("digest-of-the-shown-inspection", request.ConfirmedInspectionDigest);
    }

    [Theory]
    [InlineData(OldDatabaseDisposalReason.MigrationConfirmationRequired)]
    [InlineData(OldDatabaseDisposalReason.InUse)]
    [InlineData(OldDatabaseDisposalReason.TargetUnreadable)]
    public async Task StartRejectedOnReinspection_AsksForANewInspection(OldDatabaseDisposalReason reason)
    {
        Pending(FakeOldDatabaseDisposalService.SqliteInspection());
        _disposal.StartResult = new OldDatabaseDisposalResult(
            OldDatabaseDisposalOutcome.Rejected, "照合の時点から結果が変わりました（テスト）。", reason, OldDatabaseDisposalMethod.Delete);
        var cut = Render();
        await InspectAsync(cut);
        MethodSelect(cut).Change("delete");

        var executing = ClickExecuteAsync(cut);
        WaitForDialog(cut);
        await ConfirmAsync(cut);
        await executing;

        cut.WaitForAssertion(() => Assert.Contains(_disposal.StartResult.Message, cut.Markup), WaitTimeout);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalExecute, cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalInspect, cut.Markup);
    }

    [Theory]
    [InlineData(OldDatabaseDisposalReason.InUse, UiText.OldDatabaseDisposalInspectionInUse)]
    [InlineData(OldDatabaseDisposalReason.TargetUnreadable, UiText.OldDatabaseDisposalInspectionUnreadable)]
    [InlineData(OldDatabaseDisposalReason.DisposalRunning, UiText.OldDatabaseDisposalInspectionUnavailable)]
    public async Task InspectionThatCouldNotReadTheFile_SaysWhy_AndOffersOnlyAnotherInspection(OldDatabaseDisposalReason reason, string expected)
    {
        Pending(new OldDatabaseDisposalInspection(
            FakeOldDatabaseDisposalService.Pending(),
            OldDatabaseTargetKind.SqliteDatabase,
            null,
            ArchiveAllowed: false,
            null, null, null, null, null, null, null, 0,
            UnavailableReason: reason));
        var cut = Render();

        await InspectAsync(cut, expected);

        Assert.DoesNotContain(UiText.OldDatabaseDisposalTargetUnverifiable, cut.Markup);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalExecute, cut.Markup);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalMethodLabel, cut.Markup);
    }

    [Fact]
    public void MigrationStatusUnavailable_IsNotShownAsNoMigrationTarget()
    {
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.StatusUnavailable);
        _disposal.Status = FakeOldDatabaseDisposalService.Pending();

        var cut = Render();

        Assert.Contains(UiText.MigrationStatusUnavailable, cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalMigrationUnavailable, cut.Markup);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalMigrationNoSource, cut.Markup);
    }

    [Fact]
    public void Result_WhenTheCleanupOfAnEarlierRunWasSkipped_TellsTheAdministratorWhereToLook()
    {
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.NoSourceDatabase);
        _disposal.Status = FakeOldDatabaseDisposalService.NotApplicable(new OldDatabaseDisposalResult(
            OldDatabaseDisposalOutcome.Completed,
            "以前の保存先のファイルを書き出して保管し、元のファイルを削除しました。",
            Method: OldDatabaseDisposalMethod.Archive,
            ArchiveFilePath: @"D:\Secure\yagura-archive-20261116T000000Z\yagura-archive-20261116T000000Z.db",
            ArchiveSha256: new string('a', 64),
            UncheckedArchiveParentDirectory: @"D:\Secure"));

        var cut = Render();

        Assert.Contains(
            string.Format(UiText.OldDatabaseDisposalResultCleanupSkippedFormat, @"D:\Secure", OldDatabaseArchiveNaming.WorkDirectoryPrefix),
            cut.Markup);
    }

    [Fact]
    public async Task Unmigrated_CancelledDialog_DoesNotStart()
    {
        Pending(FakeOldDatabaseDisposalService.SqliteInspection(FakeOldDatabaseDisposalService.UnmigratedAfterSwitch()));
        var cut = Render();
        await InspectAsync(cut);
        MethodSelect(cut).Change("delete");

        var executing = ClickExecuteAsync(cut);
        WaitForDialog(cut);
        await ButtonByText(cut, UiText.ConfirmDialogCancel).ClickAsync(new MouseEventArgs());
        await executing;

        Assert.Empty(_disposal.StartRequests);
    }

    [Fact]
    public async Task Archive_ShowsTheDestinationConditionsBeforeInput_AndTheLogsBeyondRetention()
    {
        Pending(FakeOldDatabaseDisposalService.SqliteInspection(retentionDays: 30, beyondRetention: 77));
        var cut = Render();
        await InspectAsync(cut);

        MethodSelect(cut).Change("archive");

        var markup = cut.Markup;
        Assert.Contains(UiText.OldDatabaseDisposalDestinationLabel, markup);
        var conditions = string.Format(UiText.OldDatabaseDisposalDestinationConditionsFormat, "1 GB");
        Assert.Contains(conditions, markup);
        Assert.True(
            markup.IndexOf(conditions, StringComparison.Ordinal) < markup.IndexOf("<input", markup.IndexOf(conditions, StringComparison.Ordinal), StringComparison.Ordinal),
            "保管先の条件は入力欄の前に示す。");
        Assert.Contains(string.Format(UiText.OldDatabaseDisposalBeyondRetentionFormat, 30, 77), markup);

        // 保管先を確かめるまでは実行できない。
        Assert.True(ButtonByText(cut, UiText.OldDatabaseDisposalExecute).HasAttribute("disabled"));
    }

    [Fact]
    public async Task Archive_ConfirmsDestinationFileNameAndResponsibility_AndStartsWithTheDestination()
    {
        const string destination = @"D:\Archive";
        Pending(FakeOldDatabaseDisposalService.SqliteInspection());
        var cut = Render();
        await InspectAsync(cut);
        MethodSelect(cut).Change("archive");
        DestinationInput(cut).Change(destination);
        await ButtonByText(cut, UiText.OldDatabaseDisposalDestinationCheck).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Contains(_disposal.DestinationCheck.Message, cut.Markup), WaitTimeout);
        Assert.Equal([destination], _disposal.CheckedDestinations);

        var executing = ClickExecuteAsync(cut);
        var dialog = WaitForDialog(cut);
        var text = dialog.TextContent;

        Assert.Contains(UiText.OldDatabaseDisposalConfirmSummaryArchive, text);
        Assert.Contains(destination, text);
        Assert.Contains(OldDatabaseArchiveNaming.WorkDirectoryPrefix, text);
        Assert.Contains(UiText.OldDatabaseDisposalResponsibility, text);
        Assert.True(
            text.IndexOf(UiText.OldDatabaseDisposalConfirmSummaryArchive, StringComparison.Ordinal)
                < text.IndexOf(UiText.OldDatabaseDisposalResponsibility, StringComparison.Ordinal),
            text);

        await ConfirmAsync(cut);
        await executing;

        var request = Assert.Single(_disposal.StartRequests);
        Assert.Equal(OldDatabaseDisposalMethod.Archive, request.Method);
        Assert.Equal(destination, request.ArchiveParentDirectory);
    }

    [Fact]
    public async Task Archive_DestinationRejectedForCreation_ShowsTheGrantAndRevokeCommands()
    {
        const string destination = @"D:\Archive";
        Pending(FakeOldDatabaseDisposalService.SqliteInspection());
        _disposal.StartResult = new OldDatabaseDisposalResult(
            OldDatabaseDisposalOutcome.Rejected,
            "サービスの実行アカウントが保管先にフォルダを作れません。",
            OldDatabaseDisposalReason.DestinationNotWritable,
            OldDatabaseDisposalMethod.Archive,
            GrantCommand: @"icacls ""D:\Archive"" /grant ""NT SERVICE\Yagura:(AD)""",
            RevokeCommand: @"icacls ""D:\Archive"" /remove:g ""NT SERVICE\Yagura""");
        var cut = Render();
        await InspectAsync(cut);
        MethodSelect(cut).Change("archive");
        DestinationInput(cut).Change(destination);
        await ButtonByText(cut, UiText.OldDatabaseDisposalDestinationCheck).ClickAsync(new MouseEventArgs());

        var executing = ClickExecuteAsync(cut);
        WaitForDialog(cut);
        await ConfirmAsync(cut);
        await executing;

        cut.WaitForAssertion(() => Assert.Contains(UiText.OldDatabaseDisposalRejectedTitle, cut.Markup), WaitTimeout);
        Assert.Contains(_disposal.StartResult.Message, cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalGrantCommandLabel, cut.Markup);
        Assert.Contains("/grant", cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalRevokeCommandLabel, cut.Markup);
    }

    [Fact]
    public async Task UnverifiableBody_OffersDeleteOnly_AndRequiresTheExplicitConfirmation()
    {
        Pending(new OldDatabaseDisposalInspection(
            FakeOldDatabaseDisposalService.Pending(),
            OldDatabaseTargetKind.UnverifiableBody,
            null,
            ArchiveAllowed: false,
            null, null, null, null, null, null, null, 0));
        var cut = Render();
        await InspectAsync(cut);

        Assert.Contains(UiText.OldDatabaseDisposalTargetUnverifiable, cut.Markup);
        Assert.Contains(UiText.OldDatabaseDisposalArchiveNotAllowed, cut.Markup);
        Assert.DoesNotContain("value=\"archive\"", cut.Markup);

        MethodSelect(cut).Change("delete");
        var executing = ClickExecuteAsync(cut);
        WaitForDialog(cut);
        Assert.Contains(UiText.OldDatabaseDisposalConfirmUnverifiableLabel, cut.Find(".mud-dialog").TextContent);
        Assert.True(ConfirmButton(cut).HasAttribute("disabled"));
        cut.Find(".mud-dialog input[type=checkbox]").Change(true);
        cut.WaitForAssertion(() => Assert.False(ConfirmButton(cut).HasAttribute("disabled")), WaitTimeout);
        await ConfirmAsync(cut);
        await executing;

        var request = Assert.Single(_disposal.StartRequests);
        Assert.True(request.ConfirmDeleteUnverifiableFile);
        Assert.Equal(OldDatabaseDisposalMethod.Delete, request.Method);
    }

    [Fact]
    public async Task UnsafeTarget_ShowsTheReasonAndManualGuidance_AndOffersNoOperation()
    {
        Pending(new OldDatabaseDisposalInspection(
            FakeOldDatabaseDisposalService.Pending(),
            OldDatabaseTargetKind.Unsafe,
            "再解析ポイントです",
            ArchiveAllowed: false,
            null, null, null, null, null, null, null, 0));
        var cut = Render();
        var unsafeMessage = string.Format(UiText.OldDatabaseDisposalTargetUnsafeFormat, "再解析ポイントです");

        await InspectAsync(cut, unsafeMessage);

        Assert.DoesNotContain(UiText.OldDatabaseDisposalExecute, cut.Markup);
        Assert.DoesNotContain(UiText.OldDatabaseDisposalMethodLabel, cut.Markup);
    }

    [Fact]
    public async Task Started_FollowsTheRunToItsResult_WithoutReloadingThePage()
    {
        // 画面を離れても完走する処分の進み具合と結果は、状態の定期取得で示す。
        Pending(FakeOldDatabaseDisposalService.SqliteInspection());
        _disposal.StatusAfterStart = FakeOldDatabaseDisposalService.Running(OldDatabaseDisposalStage.Deleting, OldDatabaseDisposalMethod.Delete);
        var cut = Render();
        await InspectAsync(cut);
        MethodSelect(cut).Change("delete");

        var executing = ClickExecuteAsync(cut);
        WaitForDialog(cut);
        await ConfirmAsync(cut);
        await executing;

        cut.WaitForAssertion(() => Assert.Contains(UiText.OldDatabaseDisposalStageDeleting, cut.Markup), WaitTimeout);

        _disposal.Status = FakeOldDatabaseDisposalService.NotApplicable(
            new OldDatabaseDisposalResult(OldDatabaseDisposalOutcome.Completed, "以前の保存先のファイルを削除しました。"));
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.NoSourceDatabase);

        cut.WaitForAssertion(() => Assert.Contains(UiText.OldDatabaseDisposalCompletedTitle, cut.Markup), WaitTimeout);
        Assert.Contains("以前の保存先のファイルを削除しました。", cut.Markup);
    }

    // ---- 文言 ----

    [Fact]
    public void DisposalTexts_NeverCallTheOldFileExport_Evacuation()
    {
        // 旧ファイルの退避は画面では「書き出して保管」と呼ぶ——一時保管への退避と取り違えないため（ui.md §7.2）。
        var texts = typeof(UiText)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Where(field => field.Name.Contains("OldDatabaseDisposal", StringComparison.Ordinal)
                            || field.Name.StartsWith("Migration", StringComparison.Ordinal)
                            || field.Name.StartsWith("PromotionSwitch", StringComparison.Ordinal))
            .Select(field => (field.Name, Value: (string)field.GetRawConstantValue()!))
            .ToList();

        Assert.NotEmpty(texts);
        Assert.All(texts, text => Assert.DoesNotContain("退避", text.Value));
    }

    // ------------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------------

    private void Pending(OldDatabaseDisposalInspection inspection)
    {
        _migration.Status = new LogMigrationStatus(LogMigrationAvailability.Completed);
        _disposal.Status = FakeOldDatabaseDisposalService.Pending();
        _disposal.Inspection = inspection;
    }

    private IRenderedComponent<ProviderHost> Render() => _ctx.Render<ProviderHost>();

    private static async Task InspectAsync(IRenderedComponent<ProviderHost> cut, string? expectedAfterInspection = null)
    {
        await ButtonByText(cut, UiText.OldDatabaseDisposalInspect).ClickAsync(new MouseEventArgs());
        var expected = expectedAfterInspection ?? UiText.OldDatabaseDisposalMethodLabel;
        cut.WaitForAssertion(() => Assert.Contains(expected, cut.Markup), WaitTimeout);
    }

    private static IElement MethodSelect(IRenderedComponent<ProviderHost> cut) =>
        cut.FindAll("select").Single(select => select.TextContent.Contains(UiText.OldDatabaseDisposalMethodEmptyOption, StringComparison.Ordinal));

    private static IElement DestinationInput(IRenderedComponent<ProviderHost> cut) =>
        cut.FindAll("input").Single(input => input.GetAttribute("placeholder") == UiText.OldDatabaseDisposalDestinationPlaceholder);

    private static Task ClickExecuteAsync(IRenderedComponent<ProviderHost> cut)
    {
        var execute = ButtonByText(cut, UiText.OldDatabaseDisposalExecute);
        Assert.False(execute.HasAttribute("disabled"), "実行ボタンが押せない。");
        return execute.ClickAsync(new MouseEventArgs());
    }

    private static IElement WaitForDialog(IRenderedComponent<ProviderHost> cut)
    {
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mud-dialog")), WaitTimeout);
        return cut.Find(".mud-dialog");
    }

    private static IElement ConfirmButton(IRenderedComponent<ProviderHost> cut) =>
        cut.FindAll(".mud-dialog button").Single(button => button.TextContent.Trim() == UiText.OldDatabaseDisposalConfirmAction);

    private static async Task ConfirmAsync(IRenderedComponent<ProviderHost> cut) =>
        await ConfirmButton(cut).ClickAsync(new MouseEventArgs());

    private static IElement ButtonByText(IRenderedComponent<ProviderHost> cut, string text) =>
        cut.FindAll("button").First(button => button.TextContent.Trim() == text);

    /// <summary>確認ダイアログを描画できるよう、実アプリの MainLayout と同じくプロバイダを同居させる。</summary>
    private sealed class ProviderHost : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<PromotionWizardScreen>(2);
            builder.CloseComponent();
        }
    }

    /// <summary>昇格の再起動後に開いた画面（切替のセクションは初期状態のまま）を返すフェイク。</summary>
    private sealed class IdlePromotionWizardService : IPromotionWizardService
    {
        private static readonly PromotionWizardSnapshot Snapshot = new(
            InputMode: PromotionConnectionInputMode.Form,
            Form: null,
            RawConnectionString: null,
            ServiceAccountName: @"NT SERVICE\Yagura",
            HasPassword: false,
            ConnectionValidated: false,
            ExecuteIdempotencyToken: null,
            Executed: false,
            CredentialReentryRequired: false);

        public Task<PromotionWizardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<PromotionWizardSnapshot> SetConnectionFormAsync(
            PromotionConnectionForm form,
            string? password = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PromotionWizardSnapshot> SetRawConnectionStringAsync(
            string connectionString,
            string? password = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PromotionValidationResult> ValidateConnectionAsync(
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PromotionApplyResult> ExecuteAsync(
            string idempotencyToken,
            string? operatorAddress = null,
            string? operatorScheme = null,
            string? operatorPrincipal = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ScriptedLogMigrationService : ILogMigrationService
    {
        internal LogMigrationStatus Status { get; set; } = new(LogMigrationAvailability.NotPromoted);

        public Task<LogMigrationStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<LogMigrationResult> RunAsync(
            string? operatorAddress,
            string? authenticationScheme,
            string? authenticatedPrincipal,
            IProgress<LogMigrationProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SilentNotifier : IYaguraNotifier
    {
        public void NotifyOk(string message)
        {
        }

        public void NotifyInfo(string message)
        {
        }

        public void NotifyWarning(string message)
        {
        }

        public void NotifyError(string message)
        {
        }
    }
}
