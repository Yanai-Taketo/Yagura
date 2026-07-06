namespace Yagura.Abstractions.Administration;

/// <summary>
/// SQL Server 接続検証の失敗の分類（database.md §6.1——原因別の次の一手を画面に示すための
/// 構造化。分類の根拠 = Microsoft Learn 公式ドキュメント確認 2026-07-07）。
/// </summary>
/// <remarks>
/// 分類できない失敗は <see cref="Unclassified"/> に落とし、画面は生のエラーメッセージ +
/// 汎用案内に留める——**誤分類しても間違った修復 SQL を断定提示しない**安全側の設計
/// （database.md §6.1）。
/// </remarks>
public enum PromotionConnectionFailureKind
{
    /// <summary>失敗していない（検証成功）。</summary>
    None,

    /// <summary>
    /// サーバ証明書が信頼されない（SChannel <c>SEC_E_UNTRUSTED_ROOT</c> 0x80090325。
    /// 自己署名証明書等）。次の一手 =「サーバ証明書を信頼する」の有効化。
    /// </summary>
    CertificateNotTrusted,

    /// <summary>
    /// サーバへ到達できない（タイムアウト・名前解決不能・ポート閉塞）。次の一手 =
    /// サーバ名・ポート・ファイアウォールの確認（修復 SQL なし）。
    /// </summary>
    ServerUnreachable,

    /// <summary>
    /// ログイン失敗（SQL Server エラー 18456）。18456 は誤パスワードでも DB 不在
    /// （state 38/46——クライアントへ返る情報は意図的に詳細を隠す）でも返るため、
    /// 案内は条件付き（パスワード再確認 → 未作成ならログイン作成 SQL）とする。
    /// </summary>
    LoginFailed,

    /// <summary>
    /// データベースを開けない（SQL Server エラー 4060 = "Cannot open database ...
    /// requested by the login"）。次の一手 = DB 作成を含む修復 SQL。
    /// </summary>
    DatabaseNotFound,

    /// <summary>分類できない失敗（生メッセージ + 汎用案内のみ。修復 SQL は提示しない）。</summary>
    Unclassified,
}
