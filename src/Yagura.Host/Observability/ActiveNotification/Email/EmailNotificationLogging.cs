using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>
/// メール通知シンク（<see cref="EmailNotificationLoggerProvider"/>）のロギングパイプラインへの
/// 結線（ADR-0017 決定 7）。<c>Program</c> とテストが同じ結線を共有するために切り出してある
/// ——本メソッドを経由しない登録は決定 7 の不変条件（下記）を保証しない。
/// </summary>
internal static class EmailNotificationLogging
{
    /// <summary>
    /// メール通知シンクを登録する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>決定 7 の不変条件「利用者の <c>Logging:*</c> 設定がメールを止めない」は、明示の
    /// フィルタ規則で成立させる</b>: <see cref="ILoggerFactory"/> のフィルタ規則は登録経路に
    /// かかわらず全プロバイダへ適用される（<c>AddProvider</c> と
    /// <c>Services.AddSingleton&lt;ILoggerProvider&gt;</c> は等価であり、後者でもフィルタは
    /// 迂回できない——プロバイダ自身の <c>IsEnabled</c> に到達する前に、集約ロガーが規則側の
    /// MinLevel で配送を止める）。そのため、本プロバイダを名指しした規則
    /// （<see cref="FilterLoggingBuilderExtensions.AddFilter{T}(ILoggingBuilder, string, LogLevel)"/>）を
    /// ここで積む——プロバイダ指定の規則はカテゴリのみの利用者規則（<c>Logging:LogLevel:*</c>）より
    /// 常に優先されるため、イベントログのノイズ調整のつもりの設定変更が黙ってメールを止める
    /// 経路が閉じる。この経路の実配線は <c>EmailNotificationLoggingTests</c> が
    /// allowlist の全 ID について機械検証する。
    /// </para>
    /// </remarks>
    internal static ILoggingBuilder AddEmailNotificationSink(
        this ILoggingBuilder logging, EmailNotificationQueue queue)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(queue);

        logging.Services.AddSingleton<ILoggerProvider>(_ => new EmailNotificationLoggerProvider(queue));
        logging.AddFilter<EmailNotificationLoggerProvider>(category: null, LogLevel.Trace);

        return logging;
    }
}
