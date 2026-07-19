namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>
/// 送信失敗を平易な日本語の説明と「次の一手」へ写像する（ADR-0017 委任 12）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ生の SMTP 応答だけを見せないのか</b>: 決定 8 は「結果（成功 / 失敗と理由）を画面に
/// 即時表示する」と書いているが、その「理由」が SMTP の生文字列のままだと、原因に辿り着けない
/// 利用者が<b>パスワードを打ち直してアカウントをロックする</b>——決定 3 が明示的に避けたい
/// 事態そのものになる。
/// </para>
/// <para>
/// <b>判定は応答文字列の部分一致に留め、日付やスケジュールには言及しない</b>（委任 12）。
/// 「いつ廃止される」といった案内は提供状況の変化で陳腐化し、しかも画面に出た時点では
/// 検証できない。事実（サーバがそう応答した）と次の一手だけを書く。
/// </para>
/// </remarks>
internal static class EmailSendFailureGuidance
{
    /// <summary>成功時の文言。</summary>
    internal const string Success = "テストメールを送信しました。宛先の受信箱を確認してください。";

    /// <summary>
    /// 失敗の分類とサーバ応答から、利用者向けの説明を組み立てる。
    /// </summary>
    internal static string Describe(EmailSendFailureKind kind, string? detail) => kind switch
    {
        EmailSendFailureKind.ConnectionFailed =>
            "SMTP サーバに接続できませんでした。サーバ名とポート番号、"
            + "サーバが起動していること、経路上のファイアウォールが当該ポートを通すことを確認してください。",

        EmailSendFailureKind.Timeout =>
            "SMTP サーバが時間内に応答しませんでした。サーバ名とポート番号が正しいか、"
            + "経路上のファイアウォールが応答を破棄していないかを確認してください"
            + "（接続を拒否せず無応答になる構成では、この形の失敗になります）。",

        EmailSendFailureKind.StartTlsUnavailable =>
            "暗号化（STARTTLS）を「必須」にしていますが、SMTP サーバが対応していませんでした。"
            + "送信は行っていません（平文へは降格しません）。サーバが STARTTLS に対応するポート"
            + "（多くの場合 587）を使うか、経路が信頼できる場合に限り暗号化を「自動」に変更してください。",

        EmailSendFailureKind.CertificateRejected =>
            "SMTP サーバの証明書を検証できませんでした。社内 CA や自己署名の証明書を使っている場合、"
            + "その CA 証明書をこのサーバの「信頼されたルート証明機関」に取り込む必要があります。",

        EmailSendFailureKind.AuthenticationRejected => DescribeAuthenticationRejection(detail),

        EmailSendFailureKind.RelayRejected =>
            "SMTP サーバが中継を拒否しました。差出人アドレスがサーバの許可する送信元か、"
            + "宛先ドメインへの中継が許可されているかを確認してください"
            + "（社内リレーでは、送信元 IP アドレスの許可登録が必要な場合があります）。",

        _ => "メールを送信できませんでした。サーバの応答を確認してください。",
    };

    /// <summary>
    /// 認証拒否は「打ち直させない」ことが最重要（決定 3）。SMTP AUTH 自体が無効化されている
    /// 応答を、資格情報の誤りと区別して案内する。
    /// </summary>
    private static string DescribeAuthenticationRejection(string? detail)
    {
        const string smtpAuthDisabledGuidance =
            "SMTP サーバ側で SMTP 認証（SMTP AUTH）自体が無効化されています。"
            + "**パスワードの誤りではないため、入力し直しても解決しません**"
            + "（繰り返すとアカウントがロックされることがあります）。"
            + "社内のメールリレーサーバ、またはオンプレミスの Exchange 経由での送信を検討してください。"
            + "クラウドのメールサービスへ直接送る構成では、テナント側の設定変更が必要になる場合があります"
            + "（その変更が組織のセキュリティ方針上許容されるかは、管理部門に確認してください）。";

        // 応答文字列の部分一致に留める（委任 12）。クラウド側の実装差を過度に仮定しない。
        if (detail is not null
            && (detail.Contains("SmtpClientAuthentication", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("not enabled", StringComparison.OrdinalIgnoreCase)))
        {
            return smtpAuthDisabledGuidance;
        }

        return "SMTP 認証がサーバに拒否されました。ユーザー名とパスワードを確認してください。"
            + "**同じ値で繰り返し試さないでください**——連続した認証失敗でアカウントがロックされることがあります。"
            + "サーバ側で SMTP 認証そのものが無効化されている可能性もあります"
            + "（その場合はパスワードを変えても解決しません。社内のメールリレー経由での送信を検討してください）。";
    }
}
