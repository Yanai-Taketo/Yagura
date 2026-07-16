using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yagura.Abstractions.Administration;

namespace Yagura.Host;

/// <summary>
/// SCM カスタム制御コードで設定の再読み込みを受け付ける Windows サービスライフタイム
/// （CF-5 確定 = 2026-07-16 オーナー裁定。configuration.md §3・§9。Issue #262）。
/// <c>sc control Yagura 128</c>（<see cref="ReloadConfigurationControlCode"/>）で
/// <see cref="IConfigurationReloadService.ReloadAsync"/> を起動する。
/// </summary>
/// <remarks>
/// <para>
/// <b>方式の選定（CF-5）</b>: UI（管理リスナ = loopback）を経由しない再読み込み手段として、
/// 新規の待受面（名前付きパイプ・追加ポート等）を作らず、Windows サービスの標準機構
/// （カスタム制御コード 128〜255）に乗る。制御の送信には SERVICE_USER_DEFINED_CONTROL 権限
/// （既定で管理者）が必要であり、認可を OS に委ねられる。Server Core・管理画面に入れない
/// 状況でも <c>sc control</c> 一発で手編集を反映でき、「実質サービス再起動 = 受信断」
/// （security.md SEC-D6 ②の限界）を解消する。
/// </para>
/// <para>
/// <b>結線</b>: <c>AddWindowsService</c> が登録する既定の <see cref="WindowsServiceLifetime"/> を
/// 本クラスで置き換える（Program.cs。コンソール実行時は登録されず、再読み込みは UI 経由のみ）。
/// 再読み込みサービスは構築順序の都合（ライフタイムはホスト構築の最初期に要る）で
/// <see cref="IServiceProvider"/> から遅延解決する。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class YaguraWindowsServiceLifetime : WindowsServiceLifetime
{
    /// <summary>設定再読み込みのカスタム制御コード（128〜255 の先頭を採る。利用者向け文書に固定値として記載する）。</summary>
    public const int ReloadConfigurationControlCode = 128;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<YaguraWindowsServiceLifetime> _logger;

    public YaguraWindowsServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        IServiceProvider serviceProvider)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<YaguraWindowsServiceLifetime>();
    }

    protected override void OnCustomCommand(int command)
    {
        base.OnCustomCommand(command);

        if (command != ReloadConfigurationControlCode)
        {
            return;
        }

        // SCM の制御ハンドラは即座に返す必要があるため fire-and-forget で実行する。
        // 結果はイベントログ（監査 2016 / 警告 1020・1021）に残る——SCM 経由の操作者は
        // イベントログで結果を確認する運用（sc control は結果を返せない）。
        _ = Task.Run(async () =>
        {
            try
            {
                var reloadService = _serviceProvider.GetRequiredService<IConfigurationReloadService>();
                await reloadService.ReloadAsync(
                    operatorAddress: null,
                    authenticationScheme: null,
                    authenticatedPrincipal: "(SCM control)",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // SCM 契機の再読み込み失敗はプロセスを巻き込まない（ログのみ）。
                _logger.LogError(ex, "SCM カスタム制御コード {Command} による設定再読み込みに失敗しました。", command);
            }
        });
    }
}
