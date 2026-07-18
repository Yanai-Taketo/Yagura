using Yagura.Bench.HostProcess;
using Yagura.Host.Configuration;

namespace Yagura.Bench.Tests;

/// <summary>
/// <see cref="BenchConfigurationFile"/> が書く設定ファイルを、ホスト本体が起動時に使う
/// <see cref="YaguraConfigurationWriter.Read(string)"/> で読み戻せることの回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// <b>背景</b>: <c>YaguraConfigurationOptions</c> は設定値を原則 <c>string?</c> で受け取り、
/// 解析と検証は <see cref="YaguraConfigurationLoader"/> が担う（不正値は
/// <c>ConfigurationWarning</c> を出して既定値へ縮退する）。したがってベンチが書く JSON も
/// 値を文字列にしなければならず、数値や真偽値をそのまま書くと
/// <see cref="System.Text.Json.JsonException"/> が投げられ、ホストは起動すらできない。
/// </para>
/// <para>
/// 実際に <c>WriteSpoolQuotaConfiguration</c> が <c>Spool.QuotaBytes</c> を数値のまま書いており、
/// ARM64 の <c>SpoolActivationRecovery</c> シナリオでホストが起動時に落ちていた。この型不一致は
/// コンパイル時には検出できず、シナリオを実走させて初めて（CI では数分かけて）表面化する。
/// 本テストは各 writer の出力を実際に読み戻すことで、その退行を安価に捕まえる。
/// </para>
/// </remarks>
public sealed class BenchConfigurationFileTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-bench-config-test-{Guid.NewGuid():N}");

    public BenchConfigurationFileTests()
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

    /// <summary>
    /// ベンチとホストが同じファイル名を指していなければ、設定は書かれても読まれない
    /// （黙って既定値で動いてしまい、シナリオの前提が崩れる）。
    /// </summary>
    [Fact]
    public void FileName_MatchesHostConfigurationFileName()
    {
        Assert.Equal(YaguraConfigurationLoader.ConfigurationFileName, BenchConfigurationFile.FileName);
    }

    /// <summary>
    /// 本件の退行そのもの。数値で書くと <see cref="YaguraConfigurationWriter.Read(string)"/> が
    /// 例外を投げ、ホストが起動できなくなる。
    /// </summary>
    [Fact]
    public void WriteSpoolQuotaConfiguration_HostReadsBackQuotaAsString()
    {
        BenchConfigurationFile.WriteSpoolQuotaConfiguration(_dataRoot, 4L * 1024 * 1024);

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);

        Assert.Equal("4194304", snapshot.Options.Spool?.QuotaBytes);
    }

    [Fact]
    public void WriteUdpReceiveBufferConfiguration_HostReadsBackBufferAsString()
    {
        BenchConfigurationFile.WriteUdpReceiveBufferConfiguration(_dataRoot, 8 * 1024 * 1024);

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);

        Assert.Equal("8388608", snapshot.Options.Ingestion?.Udp?.ReceiveBufferBytes);
    }

    [Fact]
    public void WriteAdminRemoteHttpsAuthLoadConfiguration_HostReadsBackValues()
    {
        BenchConfigurationFile.WriteAdminRemoteHttpsAuthLoadConfiguration(
            _dataRoot,
            certificateThumbprint: "ABCDEF0123456789ABCDEF0123456789ABCDEF01",
            adminHttpsPort: 18515);

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);

        Assert.Equal("true", snapshot.Options.Admin?.RemoteBinding?.Enabled);
        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF01", snapshot.Options.Admin?.Https?.CertificateThumbprint);
        Assert.Equal("18515", snapshot.Options.Admin?.Https?.Port);
    }

    [Fact]
    public void WriteSqlServerConfiguration_HostReadsBackValues()
    {
        const string ConnectionString = "Server=.;Database=Yagura;Integrated Security=true";

        BenchConfigurationFile.WriteSqlServerConfiguration(_dataRoot, ConnectionString);

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);

        Assert.Equal("sqlserver", snapshot.Options.Storage?.Provider);
        Assert.Equal(ConnectionString, snapshot.Options.Storage?.SqlServer?.ConnectionString);
    }
}
