using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Configuration;
using Yagura.TestSupport;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// 閲覧 UI の HTTPS（ADR-0022 決定 1）の設定解決の単体テスト。不正時挙動の条件分岐——
/// 拇印の設定有無（= 暗号化意図の証跡の有無）で、平文（無効）へ倒すか閲覧リスナを開かない
/// 縮小継続（<see cref="ViewerHttpsMode.SuppressListener"/>）へ倒すかが分かれる——を固定する。
/// </summary>
/// <remarks>
/// 実プロセスで「閲覧ポートが開かず受信は継続する」ことを固定する E2E 側は
/// <c>tests/Yagura.E2E.Tests/ViewerHttpsDegradedStartupE2ETests.cs</c>（1016 の
/// <c>IngestionTlsDegradedStartupE2ETests</c> と同じ二層構成）。
/// </remarks>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class ViewerHttpsConfigurationTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new("viewer-https-config-test");
    private string _dataRoot => _tempDir.Path;

    public ViewerHttpsConfigurationTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose() => _tempDir.Dispose();

    private void WriteConfigurationFile(string json) =>
        File.WriteAllText(Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName), json);

    private const string ValidThumbprintDisplayForm = "ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01";
    private const string ValidThumbprintNormalized = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";

    [Fact]
    public void Load_ConfigurationFileMissing_ViewerHttpsDefaultsToDisabled()
    {
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(ViewerHttpsMode.Disabled, result.Configuration.ViewerHttpsMode);
        Assert.Null(result.Configuration.ViewerHttpsCertificateThumbprint);
        Assert.Null(result.Configuration.ViewerHttpsSuppressedReason);
    }

    [Fact]
    public void Load_EnabledWithValidThumbprint_ResolvesToEnabledWithNormalizedThumbprint()
    {
        // 拇印は MMC 表示形式（コロン区切り・小文字）を正規化して受理する
        // （Admin:Https:CertificateThumbprint と同型——configuration.md §6）。
        WriteConfigurationFile($$"""
            {
                "Viewer": {
                    "Https": {
                        "Enabled": "true",
                        "CertificateThumbprint": "{{ValidThumbprintDisplayForm}}"
                    }
                }
            }
            """);

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.Equal(ViewerHttpsMode.Enabled, result.Configuration.ViewerHttpsMode);
        Assert.Equal(ValidThumbprintNormalized, result.Configuration.ViewerHttpsCertificateThumbprint);
        Assert.Null(result.Configuration.ViewerHttpsSuppressedReason);
        Assert.DoesNotContain(result.Warnings, w => w.Key.StartsWith("Viewer:Https", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_EnabledWithoutThumbprint_SuppressesListener()
    {
        // ADR-0022 決定 1: Enabled=true + 拇印未設定 → 閲覧リスナを開かない縮小継続
        // （平文では開かない——決定 2。起動失敗にもしない——受信を巻き添えにしない）。
        WriteConfigurationFile("""
            {
                "Viewer": { "Https": { "Enabled": "true" } }
            }
            """);

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.Equal(ViewerHttpsMode.SuppressListener, result.Configuration.ViewerHttpsMode);
        Assert.NotNull(result.Configuration.ViewerHttpsSuppressedReason);
    }

    [Fact]
    public void Load_EnabledWithMalformedThumbprint_SuppressesListenerAndWarns()
    {
        WriteConfigurationFile("""
            {
                "Viewer": {
                    "Https": {
                        "Enabled": "true",
                        "CertificateThumbprint": "not-a-thumbprint"
                    }
                }
            }
            """);

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.Equal(ViewerHttpsMode.SuppressListener, result.Configuration.ViewerHttpsMode);
        Assert.Null(result.Configuration.ViewerHttpsCertificateThumbprint);
        Assert.NotNull(result.Configuration.ViewerHttpsSuppressedReason);
        Assert.Contains(result.Warnings, w => w.Key == "Viewer:Https:CertificateThumbprint");
    }

    [Fact]
    public void Load_InvalidEnabledWithThumbprintConfigured_SuppressesListenerNotPlaintext()
    {
        // ADR-0022 決定 1 の核心: Enabled が真偽値として不正でも、拇印が設定済み
        // （= HTTPS を意図した証跡がある）なら平文（無効）へは倒さない——
        // Notification:Email:Smtp:Security の「暗号化の意図を黙って外さない」前例に従う。
        // Admin:Https:Enabled（不正値は無効へ）と縮退の向きが異なるのは意図的
        // （ペルソナレビュー——田中・クリス——の指摘による確定）。
        WriteConfigurationFile($$"""
            {
                "Viewer": {
                    "Https": {
                        "Enabled": "yes-please",
                        "CertificateThumbprint": "{{ValidThumbprintDisplayForm}}"
                    }
                }
            }
            """);

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.Equal(ViewerHttpsMode.SuppressListener, result.Configuration.ViewerHttpsMode);
        Assert.NotNull(result.Configuration.ViewerHttpsSuppressedReason);
        Assert.Contains(result.Warnings, w => w.Key == "Viewer:Https:Enabled");
    }

    [Fact]
    public void Load_InvalidEnabledWithMalformedThumbprint_StillSuppressesListener()
    {
        // 形式不正の拇印も「HTTPS を構成しようとした証跡」に数える（生値の有無で判定——
        // ADR-0022 決定 1 の趣旨。タイポ 2 つの重なりで平文へ落ちない）。
        WriteConfigurationFile("""
            {
                "Viewer": {
                    "Https": {
                        "Enabled": "yes-please",
                        "CertificateThumbprint": "not-a-thumbprint"
                    }
                }
            }
            """);

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.Equal(ViewerHttpsMode.SuppressListener, result.Configuration.ViewerHttpsMode);
    }

    [Fact]
    public void Load_InvalidEnabledWithoutThumbprint_FallsBackToDisabledWithWarning()
    {
        // 拇印も未設定 = HTTPS が構成された形跡がない——bool のタイポだけで閲覧リスナを
        // 全停止するのは釣り合わないため、無効（平文）+ 警告へ倒す（ADR-0022 決定 1）。
        WriteConfigurationFile("""
            {
                "Viewer": { "Https": { "Enabled": "yes-please" } }
            }
            """);

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.Equal(ViewerHttpsMode.Disabled, result.Configuration.ViewerHttpsMode);
        Assert.Null(result.Configuration.ViewerHttpsSuppressedReason);
        Assert.Contains(result.Warnings, w => w.Key == "Viewer:Https:Enabled");
    }

    [Fact]
    public void Load_DisabledWithThumbprint_RemainsDisabledWithoutWarning()
    {
        // 明示的な無効 + 拇印設定済みは正当な構成（例: 一時的に HTTPS を止めて拇印は残す——
        // configuration.md §6 の「HTTPS 無効化 = opt-in の解除」による復旧の途中状態）。
        WriteConfigurationFile($$"""
            {
                "Viewer": {
                    "Https": {
                        "Enabled": "false",
                        "CertificateThumbprint": "{{ValidThumbprintDisplayForm}}"
                    }
                }
            }
            """);

        var result = YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());

        Assert.Equal(ViewerHttpsMode.Disabled, result.Configuration.ViewerHttpsMode);
        Assert.Equal(ValidThumbprintNormalized, result.Configuration.ViewerHttpsCertificateThumbprint);
        Assert.DoesNotContain(result.Warnings, w => w.Key == "Viewer:Https:Enabled");
    }

    [Fact]
    public void Load_ViewerHttpsKeys_AreKnownKeys()
    {
        // KnownKeys 登録漏れの回帰（#439 で Admin:ForwarderKit:MsiUpload:Enabled の登録漏れが
        // 実際に起きた——新キーは未知キー警告の対象にならないこと）。
        WriteConfigurationFile($$"""
            {
                "Viewer": {
                    "Https": {
                        "Enabled": "true",
                        "CertificateThumbprint": "{{ValidThumbprintDisplayForm}}"
                    }
                }
            }
            """);

        var logger = new FakeLogger();
        _ = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.DoesNotContain(
            logger.Collector.GetSnapshot(),
            log => log.Message.Contains("未知", StringComparison.Ordinal) &&
                   log.Message.Contains("Viewer:Https", StringComparison.Ordinal));
    }
}
