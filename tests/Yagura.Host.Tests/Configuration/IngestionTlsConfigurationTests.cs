using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Configuration;
using Yagura.Ingestion.Tls;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// TLS 受信（RFC 5425。opt-in。security.md §6。Issue #137）の設定解決（<c>Ingestion:Tls:*</c>）の
/// 単体テスト。<see cref="YaguraConfigurationLoaderTests"/> と同じ構成規約（優先順位・3 分類・
/// 未知キー検出）を踏襲する。
/// </summary>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class IngestionTlsConfigurationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-tls-config-test-{Guid.NewGuid():N}");
    private readonly List<string> _environmentVariablesToClear = new();

    public IngestionTlsConfigurationTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        foreach (var name in _environmentVariablesToClear)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private void SetEnvironmentVariable(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        _environmentVariablesToClear.Add(name);
    }

    private void WriteConfigurationFile(string json) =>
        File.WriteAllText(Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName), json);

    [Fact]
    public void Load_ConfigurationFileMissing_TlsDisabledWithDefaults()
    {
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.False(result.Configuration.IngestionTlsEnabled);
        Assert.Equal(TlsSyslogListenerOptions.DefaultBindAddress, result.Configuration.IngestionTlsBindAddress);
        Assert.Equal(TlsSyslogListenerOptions.DefaultPort, result.Configuration.IngestionTlsPort);
        Assert.Null(result.Configuration.IngestionTlsCertificateThumbprint);
        Assert.False(result.Configuration.IngestionTlsBindAddressIsExplicit);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.UnknownKeys);
    }

    [Fact]
    public void Load_TlsEnabledWithValidThumbprint_ResolvesAllFields()
    {
        var thumbprint = new string('A', 40);
        WriteConfigurationFile($$"""
            {
                "Ingestion": {
                    "Tls": {
                        "Enabled": "true",
                        "Port": "6514",
                        "CertificateThumbprint": "{{thumbprint}}"
                    }
                }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.True(result.Configuration.IngestionTlsEnabled);
        Assert.Equal(6514, result.Configuration.IngestionTlsPort);
        Assert.Equal(thumbprint, result.Configuration.IngestionTlsCertificateThumbprint);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_ThumbprintWithSeparatorsAndLowercase_NormalizesToUppercaseNoSeparators()
    {
        // configuration.md §6 と同型の正規化——証明書 MMC スナップインの表示形式（コロン・ハイフン
        // 区切り・小文字）を受理する。40 桁の 16 進値をコロン区切り（2 桁ごと）で表現する。
        const string rawHex = "abcdef0123456789abcdef0123456789abcdef01";
        var withSeparators = string.Join(":", Enumerable.Range(0, rawHex.Length / 2).Select(i => rawHex.Substring(i * 2, 2)));

        WriteConfigurationFile($$"""
            {
                "Ingestion": {
                    "Tls": {
                        "CertificateThumbprint": "{{withSeparators}}"
                    }
                }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(rawHex.ToUpperInvariant(), result.Configuration.IngestionTlsCertificateThumbprint);
    }

    [Fact]
    public void Load_MalformedThumbprint_FallsBackToNullAndCollectsWarning_DoesNotThrow()
    {
        // §1「縮小側で継続」——TLS 受信は opt-in であり、不正な拇印で起動を止めない
        // （fail-closed の対象は Admin:RemoteBinding:Enabled のみ。TLS 受信は Program 側で
        // 縮小継続の扱いになる）。
        WriteConfigurationFile("""
            {
                "Ingestion": {
                    "Tls": {
                        "Enabled": "true",
                        "CertificateThumbprint": "not-a-valid-thumbprint"
                    }
                }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.True(result.Configuration.IngestionTlsEnabled);
        Assert.Null(result.Configuration.IngestionTlsCertificateThumbprint);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Ingestion:Tls:CertificateThumbprint", warning.Key);
    }

    [Fact]
    public void Load_InvalidEnabledValue_FallsBackToFalseAndCollectsWarning()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Tls": { "Enabled": "not-a-bool" } } } """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.False(result.Configuration.IngestionTlsEnabled);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Ingestion:Tls:Enabled", warning.Key);
    }

    [Fact]
    public void Load_BindAddressMalformedInFile_FallsBackToLoopbackAndCollectsWarning()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Tls": { "BindAddress": "not-an-ip" } } } """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal("127.0.0.1", result.Configuration.IngestionTlsBindAddress);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Ingestion:Tls:BindAddress", warning.Key);
    }

    [Fact]
    public void Load_PortOutOfRangeInFile_FallsBackToDefaultAndCollectsWarning_DoesNotThrow()
    {
        // UDP/TCP ポート（§1「起動失敗」）とは異なり、TLS 受信ポートは opt-in の非致命キー
        // （§1「既定値で継続」）——不正値で起動を止めない。
        WriteConfigurationFile("""{ "Ingestion": { "Tls": { "Port": "70000" } } } """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(TlsSyslogListenerOptions.DefaultPort, result.Configuration.IngestionTlsPort);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Ingestion:Tls:Port", warning.Key);
    }

    [Fact]
    public void Load_EnvironmentVariableSetsTlsPort_NoFile_EnvironmentVariableWins()
    {
        SetEnvironmentVariable(YaguraHostEnvironment.IngestionTlsPortEnvironmentVariable, "0");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(0, result.Configuration.IngestionTlsPort);
    }

    [Fact]
    public void Load_UnknownTlsKey_IsDetectedAsUnknown()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Tls": { "TypoKey": "x" } } } """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Contains("Ingestion:Tls:TypoKey", result.UnknownKeys);
    }
}
