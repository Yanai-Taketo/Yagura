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
