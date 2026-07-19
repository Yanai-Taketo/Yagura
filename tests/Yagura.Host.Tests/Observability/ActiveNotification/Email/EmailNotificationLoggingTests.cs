using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yagura.Host.Observability.ActiveNotification.Email;

namespace Yagura.Host.Tests.Observability.ActiveNotification.Email;

/// <summary>
/// メール通知シンクの<b>実配線</b>のテスト（ADR-0017 決定 7・委任 6。PR #366 レビュー対応）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EmailNotificationLoggerProvider"/> の単体（<c>IsEnabled</c> や allowlist 判定）では
/// なく、<b>本番と同じ結線（<see cref="EmailNotificationLogging.AddEmailNotificationSink"/>）で
/// 組んだ <see cref="ILoggerFactory"/> 越し</b>に検証する——集約ロガーのフィルタ規則は
/// プロバイダの手前で配送を止めるため、プロバイダ単体のテストでは「利用者の <c>Logging:*</c>
/// 設定がメールを止めない」（決定 7）を検証できない（実際、フィルタ規則を積まない登録では
/// 利用者の <c>Logging:LogLevel:Default=Error</c> が Warning 重大度の通知を黙って全停止させる
/// ——本テスト群が回帰として固定する欠陥）。
/// </para>
/// </remarks>
public sealed class EmailNotificationLoggingTests
{
    /// <summary>
    /// 本番結線と同じ形（DI + AddLogging）でファクトリを組む。<paramref name="userRules"/> で
    /// 利用者の <c>Logging:*</c> 相当のフィルタ規則を注入する。
    /// </summary>
    private static (ILoggerFactory Factory, EmailNotificationQueue Queue) CreateWiredFactory(
        Action<ILoggingBuilder>? userRules = null)
    {
        var queue = new EmailNotificationQueue();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            userRules?.Invoke(logging);
            logging.AddEmailNotificationSink(queue);
        });

        var factory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        return (factory, queue);
    }

    // ------------------------------------------------------------------
    // 決定 7: 利用者の Logging:* 設定がメールを止めない
    // ------------------------------------------------------------------

    [Fact]
    public void EveryAllowlistedEventId_ReachesTheQueue_EvenWithRestrictiveUserDefaultFilter()
    {
        // Logging:LogLevel:Default = "Error" 相当。イベントログのノイズ調整として現実的な設定で、
        // allowlist の大半（Warning 重大度）が影響を受けるかどうかの分水嶺になる。
        foreach (var eventId in EmailNotificationAllowlist.RegisteredEventIds)
        {
            var (factory, queue) = CreateWiredFactory(logging => logging.AddFilter(null, LogLevel.Error));
            var logger = factory.CreateLogger("Yagura.Host.Tests.Wiring");

            var isError = EmailNotificationAllowlist.TryGetSeverity(new EventId(eventId), out var severity)
                && severity == EmailNotificationAllowlist.Severity.Error;

            // 発火点の実レベルに合わせる（本表の重大度と発火点のレベルは現状一致している）。
            if (isError)
            {
                logger.LogError(new EventId(eventId, "test"), "配線検証 {Id}", eventId);
            }
            else
            {
                logger.LogWarning(new EventId(eventId, "test"), "配線検証 {Id}", eventId);
            }

            Assert.True(
                queue.Depth == 1,
                $"イベント ID {eventId} が利用者フィルタ（Default=Error）越しにキューへ届いていません。" +
                "AddEmailNotificationSink のプロバイダ名指しフィルタ規則が失われた可能性があります。");
        }
    }

    [Fact]
    public void AllowlistedEventId_ReachesTheQueue_EvenWhenUserSilencesTheCategory()
    {
        // カテゴリ名指しの規則（Logging:LogLevel:Yagura = "None" 相当）でも止まらない——
        // プロバイダ名指しの規則はカテゴリのみの規則より常に優先される。
        var (factory, queue) = CreateWiredFactory(logging => logging.AddFilter("Yagura", LogLevel.None));
        var logger = factory.CreateLogger("Yagura.Host.Observability.Test");

        var eventId = EmailNotificationAllowlist.RegisteredEventIds.First();
        logger.LogWarning(new EventId(eventId, "test"), "配線検証");

        Assert.Equal(1, queue.Depth);
    }

    [Fact]
    public void NonAllowlistedEventId_IsStillFilteredByTheAllowlist()
    {
        // フィルタ規則の追加が「何でもメールになる」側へ倒れていないこと——選別の実体は
        // 従来どおり allowlist である。
        var (factory, queue) = CreateWiredFactory();
        var logger = factory.CreateLogger("Yagura.Host.Observability.Test");

        logger.LogError(new EventId(999999, "not-allowlisted"), "配線検証");

        Assert.Equal(0, queue.Depth);
    }
}
