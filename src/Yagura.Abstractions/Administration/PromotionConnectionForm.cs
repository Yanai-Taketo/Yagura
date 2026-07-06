namespace Yagura.Abstractions.Administration;

/// <summary>
/// 本番昇格ウィザードの接続の項目入力（database.md §6.1「接続は項目で入力し、接続文字列は
/// サーバ側で組み立てる」）。<b>パスワードを含めない</b>——パスワードは資格情報として
/// 別引数で受け渡し、メモリ内でのみ保持する（configuration.md §5 の粒度）。
/// </summary>
/// <param name="ServerName">サーバ名（インスタンス名・ポート併記を含む。例: <c>SV01\SQLEXPRESS</c>）。</param>
/// <param name="DatabaseName">データベース名。</param>
/// <param name="AuthenticationMode">認証方式（既定は Windows 統合認証——database.md §5.1 の第一推奨）。</param>
/// <param name="UserName">SQL Server 認証時のユーザー名（Windows 統合認証では <see langword="null"/>）。</param>
/// <param name="TrustServerCertificate">
/// サーバ証明書を信頼するか（証明書の検証を省略。通信の暗号化は維持——自己署名証明書の
/// 閉域環境向け。既定は信頼しない）。
/// </param>
public sealed record PromotionConnectionForm(
    string ServerName,
    string DatabaseName,
    PromotionAuthenticationMode AuthenticationMode,
    string? UserName,
    bool TrustServerCertificate);

/// <summary>SQL Server への接続の認証方式（database.md §5.1）。</summary>
public enum PromotionAuthenticationMode
{
    /// <summary>Windows 統合認証（サービスの実行アカウントで接続。第一推奨——database.md §5.1）。</summary>
    WindowsIntegrated,

    /// <summary>SQL Server 認証（ユーザー名とパスワードで接続）。</summary>
    SqlServer,
}

/// <summary>
/// 接続の入力方式（database.md §6.1——項目入力を既定とし、直接入力を上級者向けに併存する）。
/// </summary>
public enum PromotionConnectionInputMode
{
    /// <summary>項目で入力（既定。接続文字列はサーバ側で組み立てる）。</summary>
    Form,

    /// <summary>接続文字列の直接入力（上級者向け。パスワード系キーの記載は拒否される）。</summary>
    Raw,
}
