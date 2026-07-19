using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>
/// 能動通知をメールへ流すための第 2 のシンク（ADR-0017 決定 7）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ ILoggerProvider なのか</b>: 能動通知の発火点は 1 箇所ではない
/// （<see cref="ActiveNotificationMonitor"/>・<c>PersistenceWriter</c>〔Yagura.Ingestion〕・
/// 起動時経路〔Program.cs〕・<c>AdminAuthFailureDefense</c>）。発火点直呼びのフックにすると
/// 全発火点に二重書きを足すことになり、フック契約を <c>Yagura.Abstractions</c> へ公開する必要も
/// 生じ、配線漏れが構造的に起こり得る。本方式は<b>挿入点が 1 つで、発火点のコードを一切
/// 触らない</b>——EventLog プロバイダと同じ ILogger レールに乗るため、Ingestion 発の即時通知も
/// 自動的に捕捉できる。
/// </para>
/// <para>
/// <b>利用者の <c>Logging:*</c> 設定から独立させる</b>: 本プロバイダはフィルタを介さず全カテゴリ・
/// 全レベルを受け取り、選別は <see cref="EmailNotificationAllowlist"/> のみで行う
/// （<see cref="ILoggerProvider"/> は DI 上 <c>ILoggerFactory</c> に直接足すため、
/// <c>Logging:LogLevel</c> のカテゴリ別フィルタが本プロバイダの受信を止めることはない）。
/// イベントログのノイズ調整のつもりの設定変更が<b>黙ってメールを止める</b>経路を作らない
/// ——決定 2 の「設定したのに送られていない状態を警告で必ず見せる」との一貫。
/// </para>
/// <para>
/// <b>プロバイダの契約</b>: 有界キューへの投入のみを行い、<b>ブロックせず・例外を呼び出し元へ
/// 漏らさない</b>。ロギング呼び出しは受信ホットパス上にあり得るため、ここでの I/O・待機は
/// 受信の遅延に直結する。送信の実体は <see cref="EmailNotificationDispatcher"/> が担う。
/// </para>
/// </remarks>
internal sealed class EmailNotificationLoggerProvider : ILoggerProvider
{
    private readonly EmailNotificationQueue _queue;
    private readonly TimeProvider _timeProvider;

    internal EmailNotificationLoggerProvider(EmailNotificationQueue queue, TimeProvider? timeProvider = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ILogger CreateLogger(string categoryName) =>
        new EmailNotificationLogger(_queue, _timeProvider, categoryName);

    public void Dispose()
    {
        // キューはプロバイダの所有物ではない（合成ルートが所有し、ディスパッチャと共有する）。
    }

    private sealed class EmailNotificationLogger(
        EmailNotificationQueue queue, TimeProvider timeProvider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>
        /// 全レベルを受け取る（選別は allowlist のみ——クラス remarks の「利用者設定からの独立」）。
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // EventId 未指定（= 0）のログは対象になり得ないため、allowlist 参照の前に落とす。
            if (eventId.Id == 0)
            {
                return;
            }

            try
            {
                if (!EmailNotificationAllowlist.TryGetSeverity(eventId, out _))
                {
                    return;
                }

                var message = formatter(state, exception);
                queue.TryEnqueue(eventId, BuildSubject(eventId), BuildBody(eventId, logLevel, message, exception));
            }
            catch
            {
                // 通知の失敗でロギング呼び出し元（受信ホットパスを含む）を壊さない。
                // ここで握り潰す例外はメール経路のみの問題であり、正本のイベントログは
                // 別プロバイダが既に書いている。
            }
        }

        private static string BuildSubject(EventId eventId) =>
            $"[Yagura] {eventId.Name ?? eventId.Id.ToString(CultureInfo.InvariantCulture)} (ID {eventId.Id})";

        /// <summary>
        /// 本文に載せるフィールドを固定する（ADR-0017 委任 2。以後 additive のみ）。
        /// </summary>
        /// <remarks>
        /// 載せるのは「事象の識別」と「次にどこを見ればよいか」に必要な最小限——
        /// イベント ID・名称・レベル・発生時刻・ホスト名・メッセージ本文・例外の型と要旨。
        /// <b>正本はイベントログである</b>ことを本文に明記し、メールを一次資料として扱わせない
        /// （メールは at-most-once であり、届かないことがある——決定 5）。
        /// </remarks>
        private string BuildBody(EventId eventId, LogLevel logLevel, string message, Exception? exception)
        {
            var builder = new StringBuilder();

            builder.AppendLine(CultureInfo.InvariantCulture, $"発生時刻: {timeProvider.GetUtcNow():yyyy-MM-dd HH:mm:ss} UTC");
            builder.AppendLine(CultureInfo.InvariantCulture, $"ホスト名: {Environment.MachineName}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"イベント ID: {eventId.Id} ({eventId.Name})");
            builder.AppendLine(CultureInfo.InvariantCulture, $"レベル: {logLevel}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"種別: {categoryName}");
            builder.AppendLine();
            builder.AppendLine(message);

            if (exception is not null)
            {
                builder.AppendLine();
                builder.AppendLine(CultureInfo.InvariantCulture, $"例外: {exception.GetType().Name}: {exception.Message}");
            }

            builder.AppendLine();
            builder.AppendLine("--");
            builder.AppendLine("この通知は Yagura の能動通知（メール）です。");
            builder.AppendLine("同じ事象は Windows イベントログ（ソース: Yagura）にも記録されており、そちらが正本です。");
            builder.AppendLine("メールは配送されないことがあるため、「メールが来ない = 正常」とは限りません。");
            builder.AppendLine("チャネルの健全性は管理画面の常設カードで確認できます。");

            return builder.ToString();
        }
    }
}
