using System.Text.Json;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Administration;

/// <summary>
/// <see cref="SetupWizardService"/> の単体テスト（M8-4。Issue #71。configuration.md §3・§7）。
/// </summary>
/// <remarks>
/// 検証観点: (1) ステップ確定がサーバ側セッションに残り再開できる（§7）、(2) 適用が
/// yagura.json を生成し監査記録（2000 番台 ID 2001）が発火する、(3) 冪等トークンによる
/// 二重適用の抑止（§7 の一回性）、(4) 楽観競合の検出（§3——手編集を黙って上書きしない）。
/// </remarks>
public sealed class SetupWizardServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-setupwizard-test-{Guid.NewGuid():N}");
    private readonly RecordingAuditRecorder _audit = new();

    public SetupWizardServiceTests()
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
    public async Task ConfirmSteps_ProgressIsHeldServerSide_AndResumable()
    {
        var service = new SetupWizardService(_dataRoot, _audit);

        await ConfirmReceptionAsync(service);

        // 「circuit 喪失後の再入」相当: 別の呼び出し経路から取得しても確定済みステップが残る
        // （進行状態は circuit ではなくサーバ側にある——configuration.md §7）。
        var snapshot = await service.GetSnapshotAsync();
        Assert.Contains(SetupWizardStep.Reception, snapshot.ConfirmedSteps);
        Assert.Equal(SetupWizardStep.ViewerAccess, snapshot.NextStep);
        Assert.Equal("514", snapshot.ConfirmedValues[SetupWizardValueKeys.UdpPort]);
    }

    [Fact]
    public async Task GoBack_RevertsLastConfirmedStep_AndPreservesValues()
    {
        var service = new SetupWizardService(_dataRoot, _audit);
        await ConfirmReceptionAsync(service);
        Assert.Equal(SetupWizardStep.ViewerAccess, (await service.GetSnapshotAsync()).NextStep);

        var afterBack = await service.GoBackAsync();

        // 直前に確定した Reception の確定が取り消され、そこへ戻る。
        Assert.Equal(SetupWizardStep.Reception, afterBack.NextStep);
        Assert.DoesNotContain(SetupWizardStep.Reception, afterBack.ConfirmedSteps);
        // 入力値は保持され、戻り先フォームへ再表示できる。
        Assert.Equal("514", afterBack.ConfirmedValues[SetupWizardValueKeys.UdpPort]);
    }

    [Fact]
    public async Task GoBack_OnFirstStep_Throws()
    {
        var service = new SetupWizardService(_dataRoot, _audit);
        await Assert.ThrowsAsync<WizardValidationException>(() => service.GoBackAsync());
    }

    [Fact]
    public async Task GoBack_FromConfirmedReview_DiscardsApplyToken()
    {
        var service = new SetupWizardService(_dataRoot, _audit);
        await ConfirmReceptionAsync(service);
        await service.ConfirmStepAsync(SetupWizardStep.ViewerAccess, new Dictionary<string, string>
        {
            [SetupWizardValueKeys.ViewerHttpPort] = "8514",
            [SetupWizardValueKeys.ViewerPublicAccess] = "Lan",
            [SetupWizardValueKeys.AdminHttpPort] = "8515",
        });
        await service.ConfirmStepAsync(SetupWizardStep.Retention, new Dictionary<string, string>
        {
            [SetupWizardValueKeys.RetentionDays] = "30",
        });
        var review = await service.ConfirmStepAsync(SetupWizardStep.Review, new Dictionary<string, string>());
        Assert.NotNull(review.ApplyIdempotencyToken);

        var afterBack = await service.GoBackAsync();

        // 確認ステップの取り消しで適用用トークンが破棄される（確認を再度やり直す）。
        Assert.Null(afterBack.ApplyIdempotencyToken);
        Assert.DoesNotContain(SetupWizardStep.Review, afterBack.ConfirmedSteps);
    }

    [Fact]
    public async Task ConfirmStep_SkippingAhead_IsRejected()
    {
        var service = new SetupWizardService(_dataRoot, _audit);

        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfirmStepAsync(SetupWizardStep.Retention, new Dictionary<string, string>
            {
                [SetupWizardValueKeys.RetentionDays] = "30",
            }));
    }

    [Fact]
    public async Task ConfirmStep_InvalidPort_IsRejectedWithoutConfirming()
    {
        var service = new SetupWizardService(_dataRoot, _audit);

        await Assert.ThrowsAsync<WizardValidationException>(() =>
            service.ConfirmStepAsync(SetupWizardStep.Reception, new Dictionary<string, string>
            {
                [SetupWizardValueKeys.UdpPort] = "70000",
                [SetupWizardValueKeys.TcpPort] = "514",
            }));

        var snapshot = await service.GetSnapshotAsync();
        Assert.Empty(snapshot.ConfirmedSteps);
    }

    [Fact]
    public async Task Apply_WritesConfigurationFile_AndRecordsAuditEvent()
    {
        var service = new SetupWizardService(_dataRoot, _audit);
        var snapshot = await ConfirmAllStepsAsync(service);

        var result = await service.ApplyAsync(snapshot.ApplyIdempotencyToken!, operatorAddress: "127.0.0.1");

        Assert.Equal(WizardApplyOutcome.Applied, result.Outcome);
        Assert.Contains("Ingestion:Udp:Port", result.ChangedKeys);
        Assert.Equal(ConfigurationApplyEffect.RestartRequired, result.RequiredEffect);

        // 設定ファイルが生成され、確定値が書かれている（ウィザードが初期設定を生成する——
        // configuration.md §1）。
        var configPath = Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName);
        Assert.True(File.Exists(configPath));

        // 保存契機②「ウィザード保存」（Issue #329）: 起動時の設定差分照合の基準も保存内容へ
        // 更新される（次回起動で今回の保存を「停止中の手編集」と誤検出しない）。
        var lastApplied = LastAppliedConfigurationSnapshotStore.TryRead(_dataRoot);
        Assert.NotNull(lastApplied);
        Assert.False(ConfigurationChangePlanner.Compare(
            lastApplied, YaguraConfigurationWriter.Read(_dataRoot).Options).HasChanges);
        using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
        Assert.Equal("514", document.RootElement.GetProperty("Ingestion").GetProperty("Udp").GetProperty("Port").GetString());
        Assert.Equal("30", document.RootElement.GetProperty("Retention").GetProperty("Days").GetString());

        // 監査記録（2000 番台 ID 2001）: 変更キーの要約を含み、操作者（接続元）が残る。
        var recorded = Assert.Single(_audit.RecordedEvents);
        Assert.Equal(AuditEventKind.ConfigurationSaved, recorded.Kind);
        Assert.Equal("127.0.0.1", recorded.RemoteAddress);
        Assert.Contains("Ingestion:Udp:Port", recorded.Detail);
    }

    [Fact]
    public async Task Apply_TcpPortAndRetentionDaysChanged_AppearInChangedKeysAndAudit()
    {
        // Issue #210 回帰テスト: ConfigurationChangePlanner.Compare が Ingestion:Tcp:Port と
        // Retention:Days を比較していなかったため、SetupWizardService.ApplyValues が現に
        // 書き換えるこれらのキーがウィザード確定のたびに「検出された変更」（UI 表示・監査記録
        // security.md §4.1 の 2001）から欠落していた（#191 の ReverseDns より到達可能性が
        // 高いギャップとして PR #209 で発見・記録）。
        var service = new SetupWizardService(_dataRoot, _audit);
        var snapshot = await ConfirmAllStepsAsync(service);

        var result = await service.ApplyAsync(snapshot.ApplyIdempotencyToken!, operatorAddress: "127.0.0.1");

        Assert.Equal(WizardApplyOutcome.Applied, result.Outcome);
        Assert.Contains("Ingestion:Tcp:Port", result.ChangedKeys);
        Assert.Contains("Retention:Days", result.ChangedKeys);

        var recorded = Assert.Single(_audit.RecordedEvents);
        Assert.Contains("Ingestion:Tcp:Port", recorded.Detail);
        Assert.Contains("Retention:Days", recorded.Detail);
    }

    [Fact]
    public async Task Apply_SameTokenTwice_DoesNotApplyTwice()
    {
        var service = new SetupWizardService(_dataRoot, _audit);
        var snapshot = await ConfirmAllStepsAsync(service);
        var token = snapshot.ApplyIdempotencyToken!;

        var first = await service.ApplyAsync(token);
        var configPath = Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName);
        var bytesAfterFirst = File.ReadAllBytes(configPath);
        var writeTimeAfterFirst = File.GetLastWriteTimeUtc(configPath);

        // 瞬断 → 再送（configuration.md §7 の一回性保証）: 再適用されず、ファイルも書き直されない。
        var second = await service.ApplyAsync(token);

        Assert.Equal(WizardApplyOutcome.Applied, first.Outcome);
        Assert.Equal(WizardApplyOutcome.AlreadyApplied, second.Outcome);
        Assert.Equal(first.ChangedKeys, second.ChangedKeys);
        Assert.Equal(bytesAfterFirst, File.ReadAllBytes(configPath));
        Assert.Equal(writeTimeAfterFirst, File.GetLastWriteTimeUtc(configPath));

        // 監査記録も 1 回だけ（実行された操作は 1 回）。
        Assert.Single(_audit.RecordedEvents);
    }

    [Fact]
    public async Task Apply_WrongToken_IsRejected()
    {
        var service = new SetupWizardService(_dataRoot, _audit);
        await ConfirmAllStepsAsync(service);

        var result = await service.ApplyAsync("not-the-issued-token");

        Assert.Equal(WizardApplyOutcome.InvalidToken, result.Outcome);
        Assert.Empty(_audit.RecordedEvents);
    }

    [Fact]
    public async Task Apply_ExternalEditAfterReview_IsDetectedAsConflict()
    {
        var service = new SetupWizardService(_dataRoot, _audit);
        var snapshot = await ConfirmAllStepsAsync(service);

        // 確認ステップ（読み込み）後の手編集（外部変更）——configuration.md §3 の楽観競合。
        var configPath = Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName);
        File.WriteAllText(configPath, """{ "Viewer": { "HttpPort": "9999" } }""");

        var result = await service.ApplyAsync(snapshot.ApplyIdempotencyToken!);

        Assert.Equal(WizardApplyOutcome.Conflict, result.Outcome);

        // 手編集の内容は上書きされていない（「黙って消えた」事故を作らない）。
        Assert.Contains("9999", File.ReadAllText(configPath));

        // 確認ステップからのやり直しになる（Review の確定が解除される）。
        var after = await service.GetSnapshotAsync();
        Assert.Equal(SetupWizardStep.Review, after.NextStep);
        Assert.Null(after.ApplyIdempotencyToken);
        Assert.Empty(_audit.RecordedEvents);
    }

    [Fact]
    public async Task BeginReconfiguration_AfterApply_UnlocksAndSeedsCurrentValues()
    {
        // Issue #248: 適用完了後の再編集開始。適用ロックが解除され、現在の設定値を種に
        // 全入力ステップ確定済みで再開できる。
        var service = new SetupWizardService(_dataRoot, _audit);
        var snapshot = await ConfirmAllStepsAsync(service);
        await service.ApplyAsync(snapshot.ApplyIdempotencyToken!, operatorAddress: "127.0.0.1");

        var reopened = await service.BeginReconfigurationAsync();

        // 適用ロックの解除 + 3 つの入力ステップは確定済み扱い（ステッパーで全ステップへ移動できる）。
        Assert.False(reopened.Applied);
        Assert.Contains(SetupWizardStep.Reception, reopened.ConfirmedSteps);
        Assert.Contains(SetupWizardStep.ViewerAccess, reopened.ConfirmedSteps);
        Assert.Contains(SetupWizardStep.Retention, reopened.ConfirmedSteps);
        Assert.DoesNotContain(SetupWizardStep.Review, reopened.ConfirmedSteps);
        Assert.Equal(SetupWizardStep.Review, reopened.NextStep);
        Assert.Null(reopened.ApplyIdempotencyToken);

        // 入力値は適用した値（= 現在の設定ファイルの内容）が種になる。
        Assert.Equal("514", reopened.ConfirmedValues[SetupWizardValueKeys.UdpPort]);
        Assert.Equal("8514", reopened.ConfirmedValues[SetupWizardValueKeys.ViewerHttpPort]);
        Assert.Equal("Lan", reopened.ConfirmedValues[SetupWizardValueKeys.ViewerPublicAccess]);
        Assert.Equal("8515", reopened.ConfirmedValues[SetupWizardValueKeys.AdminHttpPort]);
        Assert.Equal("30", reopened.ConfirmedValues[SetupWizardValueKeys.RetentionDays]);

        // 値を変えて再確定 → 確認の確定で新トークン → 2 回目の適用が成立する（一回性は
        // トークンごとに保たれる——新トークンの適用は妨げられない。configuration.md §7）。
        await service.ConfirmStepAsync(SetupWizardStep.ViewerAccess, new Dictionary<string, string>
        {
            [SetupWizardValueKeys.ViewerHttpPort] = "9514",
            [SetupWizardValueKeys.ViewerPublicAccess] = "LocalhostOnly",
            [SetupWizardValueKeys.AdminHttpPort] = "8515",
        });
        var review = await service.ConfirmStepAsync(SetupWizardStep.Review, new Dictionary<string, string>());
        Assert.NotNull(review.ApplyIdempotencyToken);
        Assert.NotEqual(snapshot.ApplyIdempotencyToken, review.ApplyIdempotencyToken);

        var second = await service.ApplyAsync(review.ApplyIdempotencyToken!, operatorAddress: "127.0.0.1");

        Assert.Equal(WizardApplyOutcome.Applied, second.Outcome);
        Assert.Contains("Viewer:HttpPort", second.ChangedKeys);

        // 変更後の値がファイルに反映され、監査記録（2001）も 2 回目が残る。
        var configPath = Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName);
        using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
        Assert.Equal("9514", document.RootElement.GetProperty("Viewer").GetProperty("HttpPort").GetString());
        Assert.Equal(2, _audit.RecordedEvents.Count);
    }

    [Fact]
    public async Task BeginReconfiguration_WhenNothingApplied_Throws()
    {
        // 未適用・設定ファイル無しでは再編集する対象がない（通常のウィザード進行で足りる）。
        var service = new SetupWizardService(_dataRoot, _audit);

        await Assert.ThrowsAsync<WizardValidationException>(() => service.BeginReconfigurationAsync());
    }

    [Fact]
    public async Task ConfirmInputStep_AfterReviewConfirmed_DiscardsReviewAndToken()
    {
        // Issue #248: 確認ステップ確定後に入力ステップを再確定したら、古い確認内容・トークンの
        // まま適用される事故を防ぐため、確認の確定とトークンを破棄する（GoBackAsync の
        // Review 破棄と同じ意味論）。
        var service = new SetupWizardService(_dataRoot, _audit);
        var snapshot = await ConfirmAllStepsAsync(service);
        var oldToken = snapshot.ApplyIdempotencyToken!;

        var afterReconfirm = await service.ConfirmStepAsync(SetupWizardStep.Reception, new Dictionary<string, string>
        {
            [SetupWizardValueKeys.UdpPort] = "1514",
            [SetupWizardValueKeys.TcpPort] = "514",
        });

        // 確認の確定とトークンは破棄される。3 つの入力ステップは確定済みのまま → 次は確認。
        Assert.Null(afterReconfirm.ApplyIdempotencyToken);
        Assert.Equal(SetupWizardStep.Review, afterReconfirm.NextStep);
        Assert.DoesNotContain(SetupWizardStep.Review, afterReconfirm.ConfirmedSteps);
        Assert.Contains(SetupWizardStep.Reception, afterReconfirm.ConfirmedSteps);
        Assert.Contains(SetupWizardStep.ViewerAccess, afterReconfirm.ConfirmedSteps);
        Assert.Contains(SetupWizardStep.Retention, afterReconfirm.ConfirmedSteps);

        // 破棄済みの古いトークンでの適用は拒否される（configuration.md §7 の一回性——
        // トークンは「確認した内容」と 1 対 1）。
        var result = await service.ApplyAsync(oldToken);
        Assert.Equal(WizardApplyOutcome.InvalidToken, result.Outcome);
        Assert.Empty(_audit.RecordedEvents);
    }

    private static async Task ConfirmReceptionAsync(SetupWizardService service) =>
        await service.ConfirmStepAsync(SetupWizardStep.Reception, new Dictionary<string, string>
        {
            [SetupWizardValueKeys.UdpPort] = "514",
            [SetupWizardValueKeys.TcpPort] = "514",
        });

    private static async Task<SetupWizardSnapshot> ConfirmAllStepsAsync(SetupWizardService service)
    {
        await ConfirmReceptionAsync(service);
        await service.ConfirmStepAsync(SetupWizardStep.ViewerAccess, new Dictionary<string, string>
        {
            [SetupWizardValueKeys.ViewerHttpPort] = "8514",
            [SetupWizardValueKeys.ViewerPublicAccess] = "Lan",
            [SetupWizardValueKeys.AdminHttpPort] = "8515",
        });
        await service.ConfirmStepAsync(SetupWizardStep.Retention, new Dictionary<string, string>
        {
            [SetupWizardValueKeys.RetentionDays] = "30",
        });
        return await service.ConfirmStepAsync(SetupWizardStep.Review, new Dictionary<string, string>());
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
