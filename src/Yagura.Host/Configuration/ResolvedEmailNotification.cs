namespace Yagura.Host.Configuration;

/// <summary>
/// SMTP 接続時の STARTTLS の扱い（設定キー <c>Notification:Email:Smtp:Security</c>。
/// ADR-0017 決定 2）。
/// </summary>
/// <remarks>
/// <b>ここで MailKit の <c>SecureSocketOptions</c> をそのまま使わない理由</b>: 設定の解決
/// （本アセンブリの構成層）は送信の実装（MailKit を参照する送信層。#350 第 2 段）に依存
/// させない——送信ライブラリを差し替えても構成層と設定ファイルの表現は変わらない、という
/// ADR-0017 の「SMTP を製品の外部境界とする」判断を型で表す。対応付けは送信層が持つ。
/// </remarks>
internal enum EmailTransportSecurity
{
    /// <summary>STARTTLS を試みない（平文。設定値 <c>none</c>）。</summary>
    None,

    /// <summary>
    /// STARTTLS を試み、サーバが対応していなければ平文で継続する（opportunistic。
    /// 設定値 <c>auto</c>。既定）。
    /// </summary>
    Auto,

    /// <summary>
    /// STARTTLS を必須とし、確立できなければ送信を失敗させる（fail-closed。
    /// 設定値 <c>required</c>）。
    /// </summary>
    Required,
}

/// <summary>
/// メール通知（ADR-0017。opt-in・既定無効）の検証済み設定。
/// </summary>
/// <remarks>
/// <para>
/// <b>本型が存在する = 送信可能な構成が揃っている</b>。必須キーの欠落・値の不正・
/// SMTP-AUTH の片側のみの指定・DPAPI 復号失敗のいずれかがあれば、
/// <see cref="YaguraConfigurationLoader"/> は本型を組み立てず <see langword="null"/> を返し、
/// 警告を積む（ADR-0017 決定 2 の縮退——機能を無効化して起動は継続する）。送信側で
/// 「Host が null なら送らない」といった再検査を書かなくて済むようにするための設計。
/// </para>
/// <para>
/// <b><see cref="Password"/> は復号済みの平文を保持する</b>。設定ファイル上の
/// <c>dpapi:&lt;Base64&gt;</c> 表現は構成の解決時に一度だけ復号する（送信のたびに DPAPI を
/// 呼ばない）。したがって本型のインスタンスはログ・診断出力・監査記録へそのまま出さないこと
/// （<see cref="ToString"/> は record の既定実装が全プロパティを展開するため、
/// 意図的に上書きしている）。
/// </para>
/// </remarks>
internal sealed record ResolvedEmailNotification(
    string From,
    IReadOnlyList<string> To,
    string SmtpHost,
    int SmtpPort,
    EmailTransportSecurity Security,
    string? Username,
    string? Password)
{
    /// <summary>
    /// SMTP-AUTH を行うか（<see cref="Username"/> と <see cref="Password"/> の両方が揃って
    /// いるときのみ true。片側のみは構成の解決時に不正として弾かれるため、ここに到達した
    /// 時点で「両方あり」か「両方なし」のいずれかである）。
    /// </summary>
    public bool UsesAuthentication => Username is not null && Password is not null;

    /// <summary>
    /// 秘密値（<see cref="Password"/>）の漏出を防ぐため、record 既定の全プロパティ展開を
    /// 上書きする。構造化ログのテンプレートへ本型を渡した場合の既定の展開経路を塞ぐのが目的。
    /// </summary>
    public override string ToString() =>
        $"ResolvedEmailNotification {{ SmtpHost = {SmtpHost}, SmtpPort = {SmtpPort}, " +
        $"Security = {Security}, UsesAuthentication = {UsesAuthentication}, To.Count = {To.Count} }}";
}
