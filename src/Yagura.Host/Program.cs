using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Storage.Sqlite;
using HostingHost = Microsoft.Extensions.Hosting.Host;

namespace Yagura.Host;

public static class Program
{
    /// <summary>
    /// データルートを上書きする環境変数名。本格的な設定基盤（JSON 設定・ウィザード）は M3。
    /// </summary>
    public const string DataRootEnvironmentVariable = "YAGURA_DATAROOT";

    public static async Task Main(string[] args)
    {
        var dataRoot = ResolveDataRoot();
        Directory.CreateDirectory(dataRoot);

        var databasePath = Path.Combine(dataRoot, "yagura.db");

        var builder = HostingHost.CreateApplicationBuilder(args);

        builder.Services.AddSingleton<ILogStore>(_ => new SqliteLogStore(databasePath));
        builder.Services.AddSingleton(new UdpSyslogListenerOptions
        {
            BindAddress = UdpSyslogListenerOptions.DefaultBindAddress,
            Port = UdpSyslogListenerOptions.DefaultPort,
        });
        builder.Services.AddSingleton(sp => new IngestionPipeline(
            sp.GetRequiredService<UdpSyslogListenerOptions>(),
            sp.GetRequiredService<ILogStore>(),
            new NoopIngressGate(),
            sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddHostedService<IngestionHostedService>();

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// データルートを解決する。既定は <c>%ProgramData%\Yagura</c>（configuration.md §2）。
    /// <see cref="DataRootEnvironmentVariable"/> 環境変数で上書きできる。
    /// </summary>
    private static string ResolveDataRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "Yagura");
    }
}
