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
        Assert.Equal(UdpSyslogListenerOptions.DefaultReceiveBufferBytes, result.Configuration.UdpReceiveBufferBytes);
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
    // 管理リスナ・閲覧リスナの公開範囲(M6-1。Issue #51)
    // ------------------------------------------------------------------

    [Fact]
    public void Load_ConfigurationFileMissing_ViewerPublicAccessDefaultsToLan()
    {
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(ViewerPublicAccess.Lan, result.Configuration.ViewerPublicAccess);
        Assert.Equal(YaguraHostEnvironment.DefaultAdminPort, result.Configuration.AdminHttpPort);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_ViewerPublicAccessLocalhostOnlyInFile_IsAccepted()
    {
        WriteConfigurationFile("""{ "Viewer": { "PublicAccess": "LocalhostOnly" } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(ViewerPublicAccess.LocalhostOnly, result.Configuration.ViewerPublicAccess);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("lan")]
    [InlineData("LAN")]
    [InlineData("Lan")]
    public void Load_ViewerPublicAccessLanCaseInsensitive_IsAccepted(string value)
    {
        WriteConfigurationFile($$"""{ "Viewer": { "PublicAccess": "{{value}}" } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(ViewerPublicAccess.Lan, result.Configuration.ViewerPublicAccess);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_ViewerPublicAccessUnknownValue_FallsBackToLocalhostOnlyNotProductDefault()
    {
        // configuration.md §1「公開範囲・bind 先の不正値は製品既定（開放側）へ落とさない」
        // ——既定は Lan（開放側）だが、不正値の縮小先は必ず LocalhostOnly（より狭い側）。
        WriteConfigurationFile("""{ "Viewer": { "PublicAccess": "Everyone" } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(ViewerPublicAccess.LocalhostOnly, result.Configuration.ViewerPublicAccess);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Viewer:PublicAccess", warning.Key);
        Assert.Equal("Everyone", warning.InvalidValue);
        Assert.Equal(nameof(ViewerPublicAccess.LocalhostOnly), warning.AppliedValue);
        Assert.False(string.IsNullOrWhiteSpace(warning.Reason));
    }

    [Fact]
    public void Load_ViewerPublicAccessWhitespace_TreatedAsUnsetAndDefaultsToLan()
    {
        WriteConfigurationFile("""{ "Viewer": { "PublicAccess": "   " } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(ViewerPublicAccess.Lan, result.Configuration.ViewerPublicAccess);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_AdminHttpPortSetInFile_UsesFileValue()
    {
        WriteConfigurationFile("""{ "Admin": { "HttpPort": "18515" } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(18515, result.Configuration.AdminHttpPort);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_AdminHttpPortOutOfRangeInFile_FallsBackToDefaultAndCollectsWarning()
    {
        WriteConfigurationFile("""{ "Admin": { "HttpPort": "99999" } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(YaguraHostEnvironment.DefaultAdminPort, result.Configuration.AdminHttpPort);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Admin:HttpPort", warning.Key);
        Assert.Equal("99999", warning.InvalidValue);
        Assert.Equal(YaguraHostEnvironment.DefaultAdminPort.ToString(), warning.AppliedValue);
    }

    [Fact]
    public void Load_EnvironmentVariableSetsAdminPort_NoFile_EnvironmentVariableWins()
    {
        SetEnvironmentVariable(YaguraHostEnvironment.AdminPortEnvironmentVariable, "18600");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(18600, result.Configuration.AdminHttpPort);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_AdminHttpPortInvalidViaEnvironmentVariable_NoFile_FallsBackToDefaultAndCollectsWarning()
    {
        SetEnvironmentVariable(YaguraHostEnvironment.AdminPortEnvironmentVariable, "not-a-port");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(YaguraHostEnvironment.DefaultAdminPort, result.Configuration.AdminHttpPort);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(YaguraHostEnvironment.AdminPortEnvironmentVariable, warning.Key);
        Assert.Equal("not-a-port", warning.InvalidValue);
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
    // Storage:SqlServer:ConnectionString の DPAPI 暗号化表現
    // （configuration.md §2。ADR-0004 決定 5「v0.1: DPAPI 完動」）
    // ------------------------------------------------------------------

    [Fact]
    public void Load_EncryptedConnectionString_DecryptsAndResolvesToSqlServer()
    {
        // DPAPI machine スコープはマシン依存のため、暗号化表現は同一プロセス内で生成する
        // （固定の暗号文資産を持たない——CI 上でもそのまま成立する round-trip 検証）。
        const string plaintext = "Server=db.example.test;Database=Yagura;User Id=sa;Password=secret!";
        var encrypted = DpapiConnectionStringProtector.Protect(plaintext);
        WriteConfigurationFile(
            $$"""
            {
              "Storage": {
                "Provider": "sqlserver",
                "SqlServer": { "ConnectionString": "{{encrypted}}" }
              }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(StorageProvider.SqlServer, result.Configuration.StorageProvider);
        Assert.Equal(plaintext, result.Configuration.SqlServerConnectionString);
        // 暗号化表現の使用は正規の状態であり警告を出さない（平文資格情報の警告とも無縁）。
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_EncryptedConnectionStringUndecryptable_FallsBackToSqliteAndCollectsStrongWarning()
    {
        // 復号失敗（改ざん・別マシンで暗号化された yagura.json のコピー）の模擬:
        // Base64 としては正しいが DPAPI 暗号文として不正な値。
        var undecryptable = "dpapi:" + Convert.ToBase64String(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        WriteConfigurationFile(
            $$"""
            {
              "Storage": {
                "Provider": "sqlserver",
                "SqlServer": { "ConnectionString": "{{undecryptable}}" }
              }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        // M5-3 の「接続文字列不備」と同じ縮小側継続（起動を止めない）。
        Assert.Equal(StorageProvider.Sqlite, result.Configuration.StorageProvider);
        Assert.Null(result.Configuration.SqlServerConnectionString);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Storage:SqlServer:ConnectionString", warning.Key);
        Assert.Contains("sqlite", warning.AppliedValue, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("復号", warning.Reason);
        // 資格情報由来の値（暗号文を含む）は警告に載せない。
        Assert.DoesNotContain(undecryptable, warning.InvalidValue);
        Assert.DoesNotContain(undecryptable, warning.Reason);
    }

    [Fact]
    public void Load_EncryptedConnectionStringMalformedBase64_FallsBackToSqlite()
    {
        WriteConfigurationFile(
            """
            {
              "Storage": {
                "Provider": "sqlserver",
                "SqlServer": { "ConnectionString": "dpapi:not-valid-base64!!" }
              }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(StorageProvider.Sqlite, result.Configuration.StorageProvider);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Storage:SqlServer:ConnectionString", warning.Key);
    }

    [Fact]
    public void Load_PlaintextCredentialConnectionString_AcceptedWithWarning()
    {
        // 手編集の平文は従来どおり受理する（2026-07-06 オーナー決定: 自動書き換えはしない）。
        // 資格情報入りの平文には警告を出す（SqlServerConnectionStringCredentialGuard の配線）。
        const string plaintext = "Server=db;Database=Yagura;User Id=sa;Password=hunter2";
        WriteConfigurationFile(
            $$"""
            {
              "Storage": {
                "Provider": "sqlserver",
                "SqlServer": { "ConnectionString": "{{plaintext}}" }
              }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        // 受理: SQL Server provider として平文のまま使用する（手編集ユーザーを壊さない）。
        Assert.Equal(StorageProvider.SqlServer, result.Configuration.StorageProvider);
        Assert.Equal(plaintext, result.Configuration.SqlServerConnectionString);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Storage:SqlServer:ConnectionString", warning.Key);
        Assert.Contains("平文", warning.Reason);
        // パスワード値そのものは警告のどのフィールドにも載せない。
        Assert.DoesNotContain("hunter2", warning.InvalidValue);
        Assert.DoesNotContain("hunter2", warning.AppliedValue);
        Assert.DoesNotContain("hunter2", warning.Reason);
    }

    [Fact]
    public void Load_PlaintextIntegratedSecurityConnectionString_AcceptedWithoutWarning()
    {
        // Windows 統合認証（第一推奨。ADR-0004 決定 5）の平文接続文字列は資格情報を含まず、
        // 警告なしでそのまま受理される（既存テストの明示的な対として固定する）。
        WriteConfigurationFile(
            """
            {
              "Storage": {
                "Provider": "sqlserver",
                "SqlServer": { "ConnectionString": "Server=.;Database=Yagura;Integrated Security=true;" }
              }
            }
            """);
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(StorageProvider.SqlServer, result.Configuration.StorageProvider);
        Assert.Empty(result.Warnings);
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
    // UDP 受信バッファサイズ（M-2。§1「既定値で継続」）
    // ------------------------------------------------------------------

    [Fact]
    public void Load_UdpReceiveBufferBytesValid_IsAccepted()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Udp": { "ReceiveBufferBytes": "4194304" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(4 * 1024 * 1024, result.Configuration.UdpReceiveBufferBytes);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_UdpReceiveBufferBytesNotNumericInFile_FallsBackToDefaultAndCollectsWarning()
    {
        WriteConfigurationFile("""{ "Ingestion": { "Udp": { "ReceiveBufferBytes": "not-a-number" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(UdpSyslogListenerOptions.DefaultReceiveBufferBytes, result.Configuration.UdpReceiveBufferBytes);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Ingestion:Udp:ReceiveBufferBytes", warning.Key);
        Assert.Equal("not-a-number", warning.InvalidValue);
        Assert.Equal(
            UdpSyslogListenerOptions.DefaultReceiveBufferBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            warning.AppliedValue);
    }

    [Fact]
    public void Load_UdpReceiveBufferBytesBelowMinimum_FallsBackToDefaultAndCollectsWarning()
    {
        // 下限（64 KiB）未満は「受信バッファを拡大する」という設定項目の目的に反するため不正値扱い。
        WriteConfigurationFile("""{ "Ingestion": { "Udp": { "ReceiveBufferBytes": "1024" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(UdpSyslogListenerOptions.DefaultReceiveBufferBytes, result.Configuration.UdpReceiveBufferBytes);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Ingestion:Udp:ReceiveBufferBytes", warning.Key);
    }

    [Fact]
    public void Load_UdpReceiveBufferBytesAboveMaximum_FallsBackToDefaultAndCollectsWarning()
    {
        // 上限（256 MiB）超過は非現実的な巨大値として不正値扱い（誤入力の桁違いを弾く安全弁）。
        WriteConfigurationFile("""{ "Ingestion": { "Udp": { "ReceiveBufferBytes": "9999999999" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(UdpSyslogListenerOptions.DefaultReceiveBufferBytes, result.Configuration.UdpReceiveBufferBytes);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Ingestion:Udp:ReceiveBufferBytes", warning.Key);
    }

    [Fact]
    public void Load_UdpReceiveBufferBytesAtMinimumBoundary_IsAccepted()
    {
        WriteConfigurationFile(
            $$"""{ "Ingestion": { "Udp": { "ReceiveBufferBytes": "{{UdpSyslogListenerOptions.MinReceiveBufferBytes}}" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(UdpSyslogListenerOptions.MinReceiveBufferBytes, result.Configuration.UdpReceiveBufferBytes);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_UdpReceiveBufferBytesAtMaximumBoundary_IsAccepted()
    {
        WriteConfigurationFile(
            $$"""{ "Ingestion": { "Udp": { "ReceiveBufferBytes": "{{UdpSyslogListenerOptions.MaxReceiveBufferBytes}}" } } }""");
        var logger = new FakeLogger();

        var result = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal(UdpSyslogListenerOptions.MaxReceiveBufferBytes, result.Configuration.UdpReceiveBufferBytes);
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
                "Udp": { "BindAddress": "0.0.0.0", "Port": "514", "ReceiveBufferBytes": "4194304" },
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
