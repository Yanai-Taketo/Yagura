using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Configuration;
using Yagura.Ingestion.Tcp;
using Yagura.Ingestion.Udp;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <see cref="YaguraConfigurationLoader"/> の単体テスト（M3-1）。
/// </summary>
/// <remarks>
/// configuration.md §1 の要求を軸に構成する: 優先順位（環境変数 &gt; 設定ファイル &gt; 既定値）、
/// 設定ファイル不在時の既定起動、不正値の 3 分類（起動失敗・既定値継続・縮小継続）それぞれの
/// 発火、未知キーの検出。一時ディレクトリを都度作成・削除し、他テストと競合しないようにする。
/// <see cref="ConfigurationEnvironmentVariableTestCollection"/> により、環境変数を読み書きする
/// 他のテストクラス（<c>YaguraConfigurationWriterTests</c> 等）と並列実行させない。
/// </remarks>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class YaguraConfigurationLoaderTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-config-test-{Guid.NewGuid():N}");
    private readonly List<string> _environmentVariablesToClear = new();

    public YaguraConfigurationLoaderTests()
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

    private void WriteConfigurationFile(string json)
    {
        File.WriteAllText(Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName), json);
    }

    // ------------------------------------------------------------------
    // 設定ファイル不在時 → 既定値で起動（ゼロ設定ファーストラン）
    // ------------------------------------------------------------------

    [Fact]
    public void Load_ConfigurationFileMissing_UsesDefaultsAndProducesNoWarnings()
    {
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(UdpSyslogListenerOptions.DefaultBindAddress, result.Configuration.UdpBindAddress);
        Assert.Equal(UdpSyslogListenerOptions.DefaultPort, result.Configuration.UdpPort);
        Assert.Equal(TcpSyslogListenerOptions.DefaultBindAddress, result.Configuration.TcpBindAddress);
        Assert.Equal(TcpSyslogListenerOptions.DefaultPort, result.Configuration.TcpPort);
        Assert.Equal(YaguraHostEnvironment.DefaultHttpPort, result.Configuration.HttpPort);
        Assert.Equal("yagura.db", result.Configuration.SqliteFileName);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.UnknownKeys);
    }

    // ------------------------------------------------------------------
    // 優先順位: 環境変数 > 設定ファイル > 既定値
    // ------------------------------------------------------------------

    [Fact]
    public void Load_NoFileNoEnvironmentVariable_FallsBackToDefaultHttpPort()
    {
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(YaguraHostEnvironment.DefaultHttpPort, result.Configuration.HttpPort);
    }

    [Fact]
    public void Load_ConfigurationFileSetsHttpPort_NoEnvironmentOverride_UsesFileValue()
    {
        WriteConfigurationFile("""{ "Viewer": { "HttpPort": "9100" } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(9100, result.Configuration.HttpPort);
    }

    [Fact]
    public void Load_EnvironmentVariableAndFileBothSetHttpPort_EnvironmentVariableWins()
    {
        WriteConfigurationFile("""{ "Viewer": { "HttpPort": "9100" } }""");
        SetEnvironmentVariable(YaguraHostEnvironment.HttpPortEnvironmentVariable, "9200");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(9200, result.Configuration.HttpPort);
    }

    [Fact]
    public void Load_EnvironmentVariableSetsUdpPort_NoFile_EnvironmentVariableWins()
    {
        SetEnvironmentVariable(YaguraHostEnvironment.UdpPortEnvironmentVariable, "5140");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(5140, result.Configuration.UdpPort);
    }

    [Fact]
    public void Load_EnvironmentVariableSetsTcpPort_NoFile_EnvironmentVariableWins()
    {
        // M4-1: YAGURA_TCP_PORT は UDP と同じ優先順位（環境変数 > 設定ファイル > 既定値）。
        SetEnvironmentVariable(YaguraHostEnvironment.TcpPortEnvironmentVariable, "5141");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(5141, result.Configuration.TcpPort);
    }

    [Fact]
    public void ResolveDataRoot_EnvironmentVariableSet_OverridesDefault()
    {
        var overridden = Path.Combine(Path.GetTempPath(), $"yagura-dataroot-test-{Guid.NewGuid():N}");
        SetEnvironmentVariable(YaguraHostEnvironment.DataRootEnvironmentVariable, overridden);

        var resolved = YaguraConfigurationLoader.ResolveDataRoot();

        Assert.Equal(overridden, resolved);
    }

    [Fact]
    public void ResolveDataRoot_NoEnvironmentVariable_UsesProgramDataYagura()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Yagura");

        var resolved = YaguraConfigurationLoader.ResolveDataRoot();

        Assert.Equal(expected, resolved);
    }

    // ------------------------------------------------------------------
    // 不正値の 3 分類: 起動失敗（受信ポート等）
    // ------------------------------------------------------------------

    [Fact]
    public void Load_UdpPortOutOfRangeInFile_ThrowsConfigurationValidationException()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Udp": { "Port": "70000" } } }""");
        var logger = new FakeLogger();

        Assert.Throws<ConfigurationValidationException>(() => YaguraConfigurationLoader.Load(_dataRoot, logger));
    }

    [Fact]
    public void Load_UdpPortNotNumericInFile_ThrowsConfigurationValidationException()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Udp": { "Port": "not-a-port" } } }""");
        var logger = new FakeLogger();

        Assert.Throws<ConfigurationValidationException>(() => YaguraConfigurationLoader.Load(_dataRoot, logger));
    }

    [Fact]
    public void Load_UdpPortOutOfRangeViaEnvironmentVariable_ThrowsConfigurationValidationException()
    {
        SetEnvironmentVariable(YaguraHostEnvironment.UdpPortEnvironmentVariable, "-1");
        var logger = new FakeLogger();

        Assert.Throws<ConfigurationValidationException>(() => YaguraConfigurationLoader.Load(_dataRoot, logger));
    }

    [Fact]
    public void Load_TcpPortOutOfRangeInFile_ThrowsConfigurationValidationException()
    {
        // M4-1: TCP ポートは UDP と同じ「起動失敗」分類（受信の成立に不可欠）。
        WriteConfigurationFile("""{ "Ingestion": { "Tcp": { "Port": "70000" } } }""");
        var logger = new FakeLogger();

        Assert.Throws<ConfigurationValidationException>(() => YaguraConfigurationLoader.Load(_dataRoot, logger));
    }

    [Fact]
    public void Load_TcpPortNotNumericInFile_ThrowsConfigurationValidationException()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Tcp": { "Port": "not-a-port" } } }""");
        var logger = new FakeLogger();

        Assert.Throws<ConfigurationValidationException>(() => YaguraConfigurationLoader.Load(_dataRoot, logger));
    }

    [Fact]
    public void Load_TcpPortOutOfRangeViaEnvironmentVariable_ThrowsConfigurationValidationException()
    {
        SetEnvironmentVariable(YaguraHostEnvironment.TcpPortEnvironmentVariable, "-1");
        var logger = new FakeLogger();

        Assert.Throws<ConfigurationValidationException>(() => YaguraConfigurationLoader.Load(_dataRoot, logger));
    }

    // ------------------------------------------------------------------
    // 不正値の 3 分類: 既定値で継続（キー・不正値・適用値の 3 点を警告収集）
    // ------------------------------------------------------------------

    [Fact]
    public void Load_HttpPortOutOfRangeInFile_FallsBackToDefaultAndCollectsWarningWithThreePoints()
    {
        WriteConfigurationFile("""{ "Viewer": { "HttpPort": "99999" } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(YaguraHostEnvironment.DefaultHttpPort, result.Configuration.HttpPort);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Viewer:HttpPort", warning.Key);
        Assert.Equal("99999", warning.InvalidValue);
        Assert.Equal(YaguraHostEnvironment.DefaultHttpPort.ToString(), warning.AppliedValue);
        Assert.False(string.IsNullOrWhiteSpace(warning.Reason));

        // 警告は ILogger 経由でも出力される（依頼の「警告は起動時に ILogger で出力」）。
        Assert.Contains(logger.Collector.GetSnapshot(), record => record.Message.Contains("Viewer:HttpPort", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_HttpPortInvalidViaEnvironmentVariable_NoFile_FallsBackToDefaultAndCollectsWarningWithThreePoints()
    {
        SetEnvironmentVariable(YaguraHostEnvironment.HttpPortEnvironmentVariable, "not-a-port");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(YaguraHostEnvironment.DefaultHttpPort, result.Configuration.HttpPort);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(YaguraHostEnvironment.HttpPortEnvironmentVariable, warning.Key);
        Assert.Equal("not-a-port", warning.InvalidValue);
        Assert.Equal(YaguraHostEnvironment.DefaultHttpPort.ToString(), warning.AppliedValue);
        Assert.False(string.IsNullOrWhiteSpace(warning.Reason));

        // 環境変数由来の不正値も設定ファイル値と同様に ILogger 経路へ乗る（黙った縮退を作らない）。
        Assert.Contains(
            logger.Collector.GetSnapshot(),
            record => record.Message.Contains(YaguraHostEnvironment.HttpPortEnvironmentVariable, StringComparison.Ordinal));
    }

    [Fact]
    public void Load_HttpPortInvalidViaEnvironmentVariable_FileHasValidValue_FallsBackToFileValueAndCollectsWarning()
    {
        WriteConfigurationFile("""{ "Viewer": { "HttpPort": "9100" } }""");
        SetEnvironmentVariable(YaguraHostEnvironment.HttpPortEnvironmentVariable, "70000");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        // フォールバック先は既定値ではなく設定ファイルの正当値（優先順位: 環境変数 > ファイル > 既定）。
        Assert.Equal(9100, result.Configuration.HttpPort);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(YaguraHostEnvironment.HttpPortEnvironmentVariable, warning.Key);
        Assert.Equal("70000", warning.InvalidValue);
        Assert.Equal("9100", warning.AppliedValue);
    }

    [Fact]
    public void Load_HttpPortValidViaEnvironmentVariable_FileHasInvalidValue_UsesEnvironmentValueWithoutFileWarning()
    {
        // 有効な環境変数が最優先の場合、設定ファイル値は適用されない（shadowed）。
        // 「適用していない値」への警告 3 点は「適用した値」の報告として不正確になるため出さない。
        WriteConfigurationFile("""{ "Viewer": { "HttpPort": "not-a-port" } }""");
        SetEnvironmentVariable(YaguraHostEnvironment.HttpPortEnvironmentVariable, "9200");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(9200, result.Configuration.HttpPort);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_SqliteFileNameContainsInvalidPathCharacters_FallsBackToDefaultAndCollectsWarning()
    {
        WriteConfigurationFile("""{ "Storage": { "SqliteFileName": "sub/dir/evil.db" } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal("yagura.db", result.Configuration.SqliteFileName);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Storage:SqliteFileName", warning.Key);
        Assert.Equal("sub/dir/evil.db", warning.InvalidValue);
        Assert.Equal("yagura.db", warning.AppliedValue);
    }

    // ------------------------------------------------------------------
    // Storage:Provider / Storage:SqlServer:ConnectionString（M5-3。Issue #47）
    // ------------------------------------------------------------------

    [Fact]
    public void Load_StorageProviderUnset_DefaultsToSqlite()
    {
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(StorageProvider.Sqlite, result.Configuration.StorageProvider);
        Assert.Null(result.Configuration.SqlServerConnectionString);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_StorageProviderUnknownValue_FallsBackToSqliteAndCollectsWarning()
    {
        WriteConfigurationFile("""{ "Storage": { "Provider": "postgresql" } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(StorageProvider.Sqlite, result.Configuration.StorageProvider);
        Assert.Null(result.Configuration.SqlServerConnectionString);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Storage:Provider", warning.Key);
        Assert.Equal("postgresql", warning.InvalidValue);
        Assert.Equal("sqlite", warning.AppliedValue);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("SqlServer")]
    [InlineData("SQLSERVER")]
    public void Load_StorageProviderSqlServerWithConnectionString_ResolvesToSqlServer(string providerValue)
    {
        WriteConfigurationFile(
            $$"""
            {
              "Storage": {
                "Provider": "{{providerValue}}",
                "SqlServer": { "ConnectionString": "Server=.;Database=Yagura;Integrated Security=true;" }
              }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(StorageProvider.SqlServer, result.Configuration.StorageProvider);
        Assert.Equal("Server=.;Database=Yagura;Integrated Security=true;", result.Configuration.SqlServerConnectionString);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_StorageProviderSqlServerWithoutConnectionString_FallsBackToSqliteAndCollectsStrongWarning()
    {
        // 設計判断（Issue #47）: 起動失敗ではなく SQLite へ縮小する——受信を止めないことを優先する。
        WriteConfigurationFile("""{ "Storage": { "Provider": "sqlserver" } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(StorageProvider.Sqlite, result.Configuration.StorageProvider);
        Assert.Null(result.Configuration.SqlServerConnectionString);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Storage:SqlServer:ConnectionString", warning.Key);
        Assert.Contains("sqlite", warning.AppliedValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_StorageProviderSqlServerWithWhitespaceConnectionString_FallsBackToSqlite()
    {
        WriteConfigurationFile(
            """
            {
              "Storage": {
                "Provider": "sqlserver",
                "SqlServer": { "ConnectionString": "   " }
              }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(StorageProvider.Sqlite, result.Configuration.StorageProvider);
        Assert.Single(result.Warnings);
    }

    // ------------------------------------------------------------------
    // 不正値の 3 分類: 縮小側で継続（bind 先。安全側 = loopback へ）
    // ------------------------------------------------------------------

    [Fact]
    public void Load_UdpBindAddressMalformedInFile_FallsBackToLoopbackAndCollectsWarning()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Udp": { "BindAddress": "not-an-ip-address" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal("127.0.0.1", result.Configuration.UdpBindAddress);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Ingestion:Udp:BindAddress", warning.Key);
        Assert.Equal("not-an-ip-address", warning.InvalidValue);
        Assert.Equal("127.0.0.1", warning.AppliedValue);
    }

    [Fact]
    public void Load_UdpBindAddressValidSpecificAddress_IsAccepted()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Udp": { "BindAddress": "192.168.1.10" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal("192.168.1.10", result.Configuration.UdpBindAddress);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_UdpBindAddressAllInterfaces_IsAcceptedAsDefault()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Udp": { "BindAddress": "0.0.0.0" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal("0.0.0.0", result.Configuration.UdpBindAddress);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_TcpBindAddressMalformedInFile_FallsBackToLoopbackAndCollectsWarning()
    {
        // M4-1: TCP bind アドレスは UDP と同じ「縮小側で継続」分類。
        WriteConfigurationFile("""{ "Ingestion": { "Tcp": { "BindAddress": "not-an-ip-address" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal("127.0.0.1", result.Configuration.TcpBindAddress);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Ingestion:Tcp:BindAddress", warning.Key);
        Assert.Equal("not-an-ip-address", warning.InvalidValue);
        Assert.Equal("127.0.0.1", warning.AppliedValue);
    }

    [Fact]
    public void Load_TcpBindAddressValidSpecificAddress_IsAccepted()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Tcp": { "BindAddress": "192.168.1.10" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal("192.168.1.10", result.Configuration.TcpBindAddress);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_TcpBindAddressAllInterfaces_IsAcceptedAsDefault()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Tcp": { "BindAddress": "0.0.0.0" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal("0.0.0.0", result.Configuration.TcpBindAddress);
        Assert.Empty(result.Warnings);
    }

    // ------------------------------------------------------------------
    // 未知キーの検出
    // ------------------------------------------------------------------

    [Fact]
    public void Load_UnknownKeyInFile_IsCollectedAndLoggedButDoesNotFailStartup()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Udp": { "Prot": "514" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        var unknownKey = Assert.Single(result.UnknownKeys);
        Assert.Equal("Ingestion:Udp:Prot", unknownKey);

        // 未知キーはタイポであっても起動を失敗させない（既定値で継続）。
        Assert.Equal(UdpSyslogListenerOptions.DefaultPort, result.Configuration.UdpPort);

        Assert.Contains(logger.Collector.GetSnapshot(), record => record.Message.Contains("Ingestion:Udp:Prot", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_KnownKeysOnly_ProducesNoUnknownKeys()
    {
        WriteConfigurationFile(
            """
            {
              "Ingestion": {
                "Udp": { "BindAddress": "0.0.0.0", "Port": "514" },
                "Tcp": { "BindAddress": "0.0.0.0", "Port": "514" }
              },
              "Viewer": { "HttpPort": "8514" },
              "Storage": { "SqliteFileName": "custom.db" }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Empty(result.UnknownKeys);
        Assert.Equal("custom.db", result.Configuration.SqliteFileName);
    }
}
