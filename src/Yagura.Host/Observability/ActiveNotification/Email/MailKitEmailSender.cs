using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Yagura.Host.Configuration;

namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>メール 1 通を SMTP で送る口（テストで差し替えるための境界）。</summary>
internal interface IEmailSender
{
    Task<EmailSendResult> SendAsync(
        ResolvedEmailNotification configuration,
        string subject,
        string body,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// MailKit による SMTP 送信（ADR-0017 決定 2・3・委任 7）。
/// </summary>
/// <remarks>
/// <para>
/// <b>接続を保持しない</b>: 送信のたびに接続・認証・切断を行う。常設接続を持たないため、
/// 設定変更は次回送信から自然に効く（決定 9 の「参照交換で足りる」の根拠）。通知は
/// 多くとも 1 時間に 10 通であり、接続確立の費用は問題にならない。
/// </para>
/// <para>
/// <b><see cref="EmailTransportSecurity"/> → <see cref="SecureSocketOptions"/> の写像は
/// ここだけが持つ</b>（構成層は MailKit を参照しない——<see cref="ResolvedEmailNotification"/>
/// の設計意図）。
/// </para>
/// </remarks>
internal sealed class MailKitEmailSender : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(
        ResolvedEmailNotification configuration,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var message = BuildMessage(configuration, subject, body);

        // 一部の宛先だけが拒否された場合に例外にせず収集する（委任 7）。MailKit の既定は
        // OnRecipientNotAccepted で例外を投げるため、記録に振り替える。
        using var client = new PartialRejectionTolerantSmtpClient();

        try
        {
            client.Timeout = (int)EmailNotificationConstants.ConnectTimeout.TotalMilliseconds;

            await client.ConnectAsync(
                configuration.SmtpHost,
                configuration.SmtpPort,
                ToSecureSocketOptions(configuration.Security),
                cancellationToken).ConfigureAwait(false);

            // UsesAuthentication と同じ条件だが、こう書くとコンパイラも両方が非 null と分かる
            // （構成の解決時に「両方あり」か「両方なし」へ正規化済み——決定 3）。
            if (configuration is { Username: { } username, Password: { } password })
            {
                await client.AuthenticateAsync(username, password, cancellationToken).ConfigureAwait(false);
            }

            client.Timeout = (int)EmailNotificationConstants.SendTimeout.TotalMilliseconds;
            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);

            return EmailSendResult.Success(client.RejectedRecipients);
        }
        catch (AuthenticationException ex)
        {
            return EmailSendResult.Failure(EmailSendFailureKind.AuthenticationRejected, Describe(ex));
        }
        catch (SslHandshakeException ex)
        {
            return EmailSendResult.Failure(EmailSendFailureKind.CertificateRejected, Describe(ex));
        }
        catch (NotSupportedException ex)
        {
            // required 指定で STARTTLS に対応していないサーバへ接続した場合（fail-closed。決定 2）。
            return EmailSendResult.Failure(EmailSendFailureKind.StartTlsUnavailable, Describe(ex));
        }
        catch (SmtpCommandException ex)
        {
            return EmailSendResult.Failure(ClassifySmtpCommandFailure(ex), Describe(ex));
        }
        catch (SmtpProtocolException ex)
        {
            return EmailSendResult.Failure(EmailSendFailureKind.Other, Describe(ex));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // MailKit のタイムアウトは取り消し要求のない OperationCanceledException として現れる。
            return EmailSendResult.Failure(EmailSendFailureKind.Timeout, "SMTP サーバが時間内に応答しませんでした。");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            return EmailSendResult.Failure(EmailSendFailureKind.ConnectionFailed, Describe(ex));
        }
    }

    private static MimeMessage BuildMessage(ResolvedEmailNotification configuration, string subject, string body)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            Body = new TextPart("plain") { Text = body },
        };

        message.From.Add(MailboxAddress.Parse(configuration.From));

        foreach (var recipient in configuration.To)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        return message;
    }

    /// <summary>
    /// 設定値（<c>none</c> / <c>auto</c> / <c>required</c>）を MailKit の接続方式へ写像する。
    /// </summary>
    private static SecureSocketOptions ToSecureSocketOptions(EmailTransportSecurity security) => security switch
    {
        EmailTransportSecurity.None => SecureSocketOptions.None,
        // opportunistic——サーバが対応していなければ平文で継続する（決定 2 の auto）。
        EmailTransportSecurity.Auto => SecureSocketOptions.StartTlsWhenAvailable,
        // 確立できなければ接続を失敗させる（決定 2 の required = fail-closed。平文へ降格しない）。
        EmailTransportSecurity.Required => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.StartTlsWhenAvailable,
    };

    private static EmailSendFailureKind ClassifySmtpCommandFailure(SmtpCommandException ex) => ex.ErrorCode switch
    {
        SmtpErrorCode.SenderNotAccepted or SmtpErrorCode.RecipientNotAccepted => EmailSendFailureKind.RelayRejected,
        SmtpErrorCode.MessageNotAccepted => EmailSendFailureKind.RelayRejected,
        _ => EmailSendFailureKind.Other,
    };

    /// <summary>
    /// 例外を記録用の文字列にする。<b>AUTH 交換の内容は含めない</b>（決定 3。資格情報が
    /// イベントログ・監査・画面へ漏れる経路を作らない）——MailKit の
    /// <see cref="AuthenticationException"/> はサーバ応答を含み得るため、認証失敗のみ
    /// メッセージを固定文言に置き換える。
    /// </summary>
    private static string Describe(Exception ex) => ex is AuthenticationException
        ? "SMTP 認証がサーバに拒否されました。（サーバ応答は資格情報を含み得るため記録しません）"
        : ex.Message;

    /// <summary>
    /// 一部の宛先が拒否されても例外にせず記録する <see cref="SmtpClient"/>（委任 7）。
    /// </summary>
    /// <remarks>
    /// 全宛先が拒否された場合は MailKit が別途 <see cref="SmtpCommandException"/> を投げるため、
    /// 「1 件も届かなかった」が成功として扱われることはない。
    /// </remarks>
    private sealed class PartialRejectionTolerantSmtpClient : SmtpClient
    {
        private readonly List<string> _rejectedRecipients = [];

        internal IReadOnlyList<string> RejectedRecipients => _rejectedRecipients;

        protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response) =>
            _rejectedRecipients.Add(mailbox.Address);
    }
}
