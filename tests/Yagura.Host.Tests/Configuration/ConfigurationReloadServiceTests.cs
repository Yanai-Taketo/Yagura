using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration;
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
        => CreateService(timeProvider: null, appliers);

    private ConfigurationReloadService CreateService(
        TimeProvider? timeProvider,
        params ImmediateConfigurationApplier[] appliers)
    {
        // 起動時スナップショット = 現時点のファイル内容（Program.cs と同じ捕捉方法）。
        var startupOptions = YaguraConfigurationWriter.Read(_dataRoot).Options;
        return new ConfigurationReloadService(
            _dataRoot, startupOptions, appliers, _auditRecorder, timeProvider,
            logger: new FakeLogger<ConfigurationReloadService>());
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

    /// <summary>
    /// 読み取り・解析の失敗（構文エラー・重複キー）も、検証失敗と同じく「適用せず旧設定のまま継続」
    /// とする（configuration.md §1。イベント ID 1021）。起動時は起動失敗だが稼働中は受信を止めない
    /// ——この非対称により、再読み込みは再起動前の安全確認として使える（§3）。
    /// </summary>
    /// <remarks>
    /// 重複キーは構文エラーではなく <see cref="InvalidDataException"/> として現れるため、
    /// <see cref="JsonException"/> だけを捕捉すると「再読み込みは通ったのに再起動で起動しない」
    /// という潜伏事故が成立する。両方を回帰対象にする（Issue #312）。
    /// </remarks>
    [Theory]
    [InlineData("""{ "Retention": { "Days": "90" """)]                              // 構文エラー（閉じ括弧なし）
    [InlineData("""{ "Retention": { "Days": "90", "Days": "30" } }""")]              // 重複キー
    public async Task Reload_UnreadableFile_RejectsAndKeepsOldConfiguration(string brokenJson)
    {
        WriteConfigurationFile("""{ }""");
        var applierCalled = false;
        var service = CreateService(new ImmediateConfigurationApplier(
            ["Retention:Days"], _ => applierCalled = true));

        WriteConfigurationFile(brokenJson);
        var result = await service.ReloadAsync(null, null, null);

        Assert.True(result.Rejected);
        Assert.NotNull(result.RejectionReason);
        Assert.False(applierCalled, "読み取りに失敗したときは一切適用しないこと。");

        // 直した後は正常に適用できる（拒否が後を引かない）。
        WriteConfigurationFile("""{ "Retention": { "Days": "90" } }""");
        var recovered = await service.ReloadAsync(null, null, null);
        Assert.False(recovered.Rejected);
        Assert.True(applierCalled);
    }

    /// <summary>
    /// 再読み込みに成功したら良好構成の写しも更新する（configuration.md §1）。
    /// </summary>
    /// <remarks>
    /// 更新契機を起動時だけにすると、再読み込みで適用済みの変更が写しに入らず、
    /// 復元したときに黙って巻き戻る——「意図しない設定で動く」事故を、復旧手段自体が
    /// 起こすことになる（PR #310 の 2 巡目レビューで 4 ペルソナが独立に指摘）。
    /// </remarks>
    [Fact]
    public async Task Reload_Success_UpdatesLastKnownGoodCopy()
    {
        WriteConfigurationFile("""{ "Retention": { "Days": "30" } }""");
        var service = CreateService(new ImmediateConfigurationApplier(["Retention:Days"], _ => { }));

        // 再読み込みで変更を適用する（再起動は挟まない）。
        WriteConfigurationFile("""{ "Retention": { "Days": "90" } }""");
        var result = await service.ReloadAsync(null, null, null);
        Assert.False(result.Rejected);

        // 写しに再読み込み後の内容が入っていること = 復元しても 90 が失われない。
        var copy = LastKnownGoodConfiguration.GetPath(_dataRoot);
        Assert.True(File.Exists(copy), "再読み込みの成功時に写しが作られること。");
        Assert.Contains("90", File.ReadAllText(copy));
    }

    /// <summary>
    /// 拒否された再読み込みでは写しを更新しない（壊れたファイルを復旧元にしない）。
    /// </summary>
    [Fact]
    public async Task Reload_Rejected_DoesNotUpdateLastKnownGoodCopy()
    {
        WriteConfigurationFile("""{ "Retention": { "Days": "30" } }""");
        var service = CreateService(new ImmediateConfigurationApplier(["Retention:Days"], _ => { }));
        await service.ReloadAsync(null, null, null);

        var copy = LastKnownGoodConfiguration.GetPath(_dataRoot);
        var before = File.ReadAllText(copy);

        WriteConfigurationFile("""{ "Retention": { "Days": "90" """); // 構文エラー
        var result = await service.ReloadAsync(null, null, null);

        Assert.True(result.Rejected);
        Assert.Equal(before, File.ReadAllText(copy));
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
    public async Task WizardApply_ThenReload_AppliesImmediateKeysAndRecordsTwoAuditEvents()
    {
        // Issue #287: ウィザード保存後の自動反映は、保存（SetupWizardService.ApplyAsync = 2001）の
        // 直後に本サービス（設定の再読み込みと同じ適用経路 = 2016）を呼ぶ合成（画面が行う）。
        // 監査は 2 件それぞれ記録される——統合しない（2026-07-18 オーナー裁定: 実際に 2 つの
        // 事象が起きており、統合すると反映失敗時の切り分けができなくなる）。
        WriteConfigurationFile("""{ "Retention": { "Days": "30" } }""");
        int? appliedDays = null;
        var reloadService = CreateService(new ImmediateConfigurationApplier(
            ["Retention:Days"], resolved => appliedDays = resolved.RetentionDays));

        var wizard = new SetupWizardService(_dataRoot, _auditRecorder);
        await wizard.ConfirmStepAsync(SetupWizardStep.Reception, new Dictionary<string, string>
        {
            [SetupWizardValueKeys.UdpPort] = "514",
            [SetupWizardValueKeys.TcpPort] = "514",
        });
        await wizard.ConfirmStepAsync(SetupWizardStep.ViewerAccess, new Dictionary<string, string>
        {
            [SetupWizardValueKeys.ViewerHttpPort] = "8514",
            [SetupWizardValueKeys.ViewerPublicAccess] = "Lan",
            [SetupWizardValueKeys.AdminHttpPort] = "8515",
        });
        await wizard.ConfirmStepAsync(SetupWizardStep.Retention, new Dictionary<string, string>
        {
            [SetupWizardValueKeys.RetentionDays] = "90",
        });
        var review = await wizard.ConfirmStepAsync(SetupWizardStep.Review, new Dictionary<string, string>());

        var applyResult = await wizard.ApplyAsync(
            review.ApplyIdempotencyToken!, "127.0.0.1", "app", "admin");
        Assert.Equal(WizardApplyOutcome.Applied, applyResult.Outcome);

        var reload = await reloadService.ReloadAsync("127.0.0.1", "app", "admin");

        // 即時キー（Retention:Days）はライブ反映され、適用の口がないキーは再起動待ちに載る。
        Assert.False(reload.Rejected);
        Assert.Contains("Retention:Days", reload.AppliedKeys);
        Assert.Equal(90, appliedDays);
        Assert.Contains("Viewer:HttpPort", reload.PendingRestartKeys);

        // 監査は 2001（保存）→ 2016（反映）の 2 件・この順。
        Assert.Equal(
            new[] { AuditEventKind.ConfigurationSaved, AuditEventKind.ConfigurationReloaded },
            _auditRecorder.Recorded.Select(e => e.Kind));
    }

    [Fact]
    public async Task GetPendingRestartKeys_EmptyInitially_AndReportsDetectionTime()
    {
        // Issue #286: 常設表示の読み取り口。再読み込みを実行していない初期状態は空で、
        // 再起動待ちキーの検出後は検出時刻付きで返る。
        WriteConfigurationFile("""{ }""");
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-18T01:00:00Z"));
        var service = CreateService(time);

        Assert.Empty(service.GetPendingRestartKeys());

        WriteConfigurationFile("""{ "Viewer": { "HttpPort": "9100" } }""");
        await service.ReloadAsync(null, null, null);

        var entry = Assert.Single(service.GetPendingRestartKeys());
        Assert.Equal("Viewer:HttpPort", entry.Key);
        Assert.Equal(DateTimeOffset.Parse("2026-07-18T01:00:00Z"), entry.DetectedAt);
    }

    [Fact]
    public async Task GetPendingRestartKeys_KeepsFirstDetectionTime_AndSortsByKey()
    {
        WriteConfigurationFile("""{ }""");
        var firstDetection = DateTimeOffset.Parse("2026-07-18T01:00:00Z");
        var time = new FakeTimeProvider(firstDetection);
        var service = CreateService(time);

        WriteConfigurationFile("""{ "Viewer": { "HttpPort": "9100" } }""");
        await service.ReloadAsync(null, null, null);

        // 同じキーが後続の再読み込みで再び変更されても、検出時刻は最初の検出のまま
        // （「いつから未反映のまま残っているか」を表す）。別キーは新しい時刻で検出される。
        time.Advance(TimeSpan.FromMinutes(10));
        WriteConfigurationFile("""{ "Viewer": { "HttpPort": "9200" }, "Storage": { "SqliteFileName": "x.db" } }""");
        await service.ReloadAsync(null, null, null);

        var entries = service.GetPendingRestartKeys();
        Assert.Equal(new[] { "Storage:SqliteFileName", "Viewer:HttpPort" }, entries.Select(e => e.Key));
        Assert.Equal(firstDetection + TimeSpan.FromMinutes(10), entries[0].DetectedAt);
        Assert.Equal(firstDetection, entries[1].DetectedAt);
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
