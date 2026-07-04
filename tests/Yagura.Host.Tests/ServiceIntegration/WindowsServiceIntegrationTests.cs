using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.EventLog;

namespace Yagura.Host.Tests.ServiceIntegration;

/// <summary>
/// Windows サービス統合・イベントログ経路（M3-2 #31）の DI 構成テスト。
/// </summary>
/// <remarks>
/// <para>
/// サービス統合そのもの（実際に Windows サービスとして起動・SCM から停止要求を受ける経路）は
/// プロセスの起動モード（<c>WindowsServiceHelpers.IsWindowsService()</c>）に依存し、単体テスト・
/// CI（コンソール実行）では原理的に再現できない。lab 検証（conventions.md「実環境依存の機能」）
/// に委ねる。
/// </para>
/// <para>
/// 本クラスが単体テストで担保するのは次の 2 点である:
/// (1) <c>AddWindowsService</c> と <c>AddEventLog</c>（+ <c>RegisterProviderOptions</c>）を
/// <see cref="Program"/> と同じ手順で組み込んだ DI コンテナが、例外なくビルドできること。
/// (2) コンソール実行と同じ本テストのプロセス文脈では <c>AddWindowsService</c> が
/// "context aware" 仕様（公式 API リファレンス。Program.cs のコメント参照）どおり
/// no-op となり、既定の <see cref="IHostLifetime"/>（<c>ConsoleLifetime</c> 相当）が
/// <c>WindowsServiceLifetime</c> に差し替わらないこと——これが「コンソール実行は従来どおり
/// 動くこと」の DI レベルでの裏付けになる。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceIntegrationTests
{
    [Fact]
    public void AddWindowsService_AndAddEventLog_ComposeWithoutException()
    {
        var builder = WebApplication.CreateBuilder();

        // ポートを固定すると他のテストプロセスと衝突しうるため、必ず 0（OS 採番）にする。
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddWindowsService(options => options.ServiceName = Program.WindowsServiceName);

        LoggerProviderOptions.RegisterProviderOptions<EventLogSettings, EventLogLoggerProvider>(builder.Services);
        builder.Logging.AddEventLog(settings => settings.SourceName = Program.WindowsServiceName);

        using var app = builder.Build();

        Assert.NotNull(app.Services.GetRequiredService<IHostApplicationLifetime>());
    }

    [Fact]
    public void AddWindowsService_WhenNotRunningAsWindowsService_LeavesDefaultHostLifetime()
    {
        // xUnit のテストプロセスは通常のコンソールプロセスであり、Windows サービスとしては
        // 起動されていない。AddWindowsService の "context aware" 仕様どおりであれば、
        // IHostLifetime は WindowsServiceLifetime に差し替わらず、既定実装
        // （ConsoleLifetime）のまま残るはずである。
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddWindowsService(options => options.ServiceName = Program.WindowsServiceName);

        using var app = builder.Build();

        var hostLifetime = app.Services.GetRequiredService<IHostLifetime>();

        Assert.DoesNotContain("WindowsServiceLifetime", hostLifetime.GetType().FullName, StringComparison.Ordinal);
    }

    [Fact]
    public void AddEventLog_RegistersEventLogLoggerProvider()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        LoggerProviderOptions.RegisterProviderOptions<EventLogSettings, EventLogLoggerProvider>(builder.Services);
        builder.Logging.AddEventLog(settings => settings.SourceName = Program.WindowsServiceName);

        using var app = builder.Build();

        var providers = app.Services.GetServices<ILoggerProvider>();

        Assert.Contains(providers, p => p is EventLogLoggerProvider);
    }

    [Fact]
    public void WindowsServiceName_IsYagura()
    {
        // サービス名は暫定値として "Yagura" を固定する（最終報告に明記。M9 のインストーラが
        // sc.exe create / New-Service の -Name にこの定数と同じ値を使う前提）。
        Assert.Equal("Yagura", Program.WindowsServiceName);
    }
}
