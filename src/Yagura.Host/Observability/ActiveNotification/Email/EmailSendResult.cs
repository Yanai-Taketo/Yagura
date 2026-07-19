namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>送信の失敗分類（画面・常設カードで平易な説明へ写像するための区別。ADR-0017 委任 12）。</summary>
/// <remarks>
/// <b>生の SMTP 応答文字列だけを利用者に見せない</b>ための型。決定 8 の「理由を画面に即時表示」
/// が生文字列のままだと、原因に辿り着けない利用者が<b>パスワードを打ち直してアカウントを
/// ロックする</b>——決定 3 が明示的に避けたい事態そのものになる。
/// </remarks>
internal enum EmailSendFailureKind
{
    /// <summary>失敗していない。</summary>
    None,

    /// <summary>SMTP サーバへ接続できない（名前解決・到達性・ポート閉塞）。</summary>
    ConnectionFailed,

    /// <summary>STARTTLS を必須にしたが確立できなかった（<c>required</c> の fail-closed）。</summary>
    StartTlsUnavailable,

    /// <summary>サーバ証明書の検証に失敗した。</summary>
    CertificateRejected,

    /// <summary>SMTP 認証が拒否された（資格情報の誤り・AUTH 自体の無効化を含む）。</summary>
    AuthenticationRejected,

    /// <summary>中継を拒否された（差出人・宛先がサーバの中継ポリシーの外）。</summary>
    RelayRejected,

    /// <summary>タイムアウト。</summary>
    Timeout,

    /// <summary>上記に分類できない失敗。</summary>
    Other,
}

/// <summary>1 通の送信の結果（ADR-0017 決定 5・委任 7・委任 12）。</summary>
/// <param name="Succeeded">メッセージとして送信が成立したか。</param>
/// <param name="FailureKind">失敗の分類（成功時は <see cref="EmailSendFailureKind.None"/>）。</param>
/// <param name="FailureDetail">
/// 記録・表示用の詳細（サーバ応答を含み得る）。<b>AUTH 交換の内容は含めない</b>（決定 3）。
/// </param>
/// <param name="RejectedRecipients">
/// 受理されなかった宛先（<see cref="Succeeded"/> が <see langword="true"/> でも空とは限らない）。
/// <b>一部拒否は「メッセージとしては送信成功・拒否宛先を警告ログ」とし再送しない</b>——
/// 成功済み宛先への二重送信を作らないため（決定 5・委任 7）。
/// </param>
internal sealed record EmailSendResult(
    bool Succeeded,
    EmailSendFailureKind FailureKind,
    string? FailureDetail,
    IReadOnlyList<string> RejectedRecipients)
{
    internal static EmailSendResult Success(IReadOnlyList<string>? rejectedRecipients = null) =>
        new(true, EmailSendFailureKind.None, null, rejectedRecipients ?? []);

    internal static EmailSendResult Failure(EmailSendFailureKind kind, string detail) =>
        new(false, kind, detail, []);
}
