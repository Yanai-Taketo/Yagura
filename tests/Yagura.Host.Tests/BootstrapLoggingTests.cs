using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;

namespace Yagura.Host.Tests;

/// <summary>
/// bootstrap ロガー（DI コンテナ構築前）の配線テスト（Issue #433）。
/// <see cref="Program.ConfigureBootstrapLogging"/> が EventLog シンクを含むことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ配線だけをテストするのか</b>: 実際に Windows イベントログへ書かれることの検証は
/// イベントソース登録（管理者権限）とマシングローバルな実ログを要し、CI では検証できない
/// （SEC-9-a と同型の制約——`installer/lab/startup-warning-regression-procedure.md` の
/// 「なぜ CI ではなく lab なのか」参照）。実機到達の回帰検証は同 lab 手順書 §C が担い、
/// 本テストは「EventLog シンクが bootstrap ロガーの構成から外れていないこと」——
/// Issue #433 の欠陥そのもの（コンソール専用の bootstrap ロガー）への退行——を CI で検知する。
/// </para>
/// <para>
/// EventLog プロバイダの実インスタンス化（<see cref="ServiceProvider"/> からの解決）は
/// OS への書き込みを伴わない（コンストラクタは <c>System.Diagnostics.EventLog</c> の生成のみ。
/// 書き込みは最初のログ出力まで遅延される）ため、テストプロセスから安全に実行できる。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BootstrapLoggingTests
{
    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(Program.ConfigureBootstrapLogging);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ConfigureBootstrapLogging_RegistersConsoleAndEventLogProviders()
    {
        using var serviceProvider = BuildServiceProvider();

        var providers = serviceProvider.GetServices<ILoggerProvider>().ToList();

        // コンソール（Generic Host 標準と同じ標準出力——E2E テストの stdout 監視の前提）と、
        // Windows イベントログ（サービス構成での唯一の観測経路——Issue #433 の本体）の両方。
        Assert.Contains(providers, p => p is ConsoleLoggerProvider);
        Assert.Contains(providers, p => p is EventLogLoggerProvider);
    }

    [Fact]
    public void ConfigureBootstrapLogging_EventLogSourceName_MatchesWindowsServiceName()
    {
        using var serviceProvider = BuildServiceProvider();

        var settings = serviceProvider.GetRequiredService<IOptions<EventLogSettings>>().Value;

        // DI 側の EventLog プロバイダ登録（Program.Main の AddEventLog）と同じソース名。
        // bootstrap 段と Host 構築後で Event Viewer 上のソースが分かれてはならない。
        Assert.Equal(Program.WindowsServiceName, settings.SourceName);
    }

    [Fact]
    public void ConfigureBootstrapLogging_EventLogProvider_IsFilteredToWarningAndAbove()
    {
        using var serviceProvider = BuildServiceProvider();

        var filterOptions = serviceProvider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;

        // DI 側の EventLog 既定フィルタ（Warning 以上）に揃える明示フィルタ。外れると
        // 設定読み込みの Information ログが起動のたびにイベントログへ流れる。
        var eventLogRule = filterOptions.Rules.SingleOrDefault(
            r => r.ProviderName == typeof(EventLogLoggerProvider).FullName);

        Assert.NotNull(eventLogRule);
        Assert.Equal(LogLevel.Warning, eventLogRule.LogLevel);
        Assert.Null(eventLogRule.CategoryName);
    }
}
