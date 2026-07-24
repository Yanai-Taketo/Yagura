namespace Yagura.Storage;

/// <summary>
/// Windows 統合認証での SQL Server 接続失敗の一次切り分け（DC 起因か SQL Server 起因か。
/// database.md §6.1 の観測性要件——ADR-0015 決定 5。Issue #418）。
/// </summary>
public enum IntegratedAuthFailureOrigin
{
    /// <summary>DC 起因（SSPI コンテキスト生成失敗——DC 到達不能・SPN 解決不能等）。</summary>
    DomainController,

    /// <summary>SQL Server 起因（ログイン未作成・DB 不在/接続権限不足）。</summary>
    SqlServer,
}

/// <summary>
/// Windows 統合認証での SQL Server 接続失敗の詳細（イベントログ警告 1031 の材料。Issue #418）。
/// </summary>
/// <param name="Origin">一次切り分け（DC 起因 / SQL Server 起因）。</param>
/// <param name="Description">失敗種別の説明（運用者向け。切り分けの次の一手を含む）。</param>
/// <param name="AccountName">実行主体（実効実行アカウント名。SQL Server 側にこの名前で見える）。</param>
/// <remarks>
/// 分類の根拠は SEC-14 (a)/(c) の AD lab 実測（2026-07-24。ADR-0015 改訂履歴 2）——推測の
/// 分類表を作らない（conventions.md「技術的主張の検証」。Issue #418 が実装を lab 実測後まで
/// 見送った理由そのもの）。Storage 層はロガーを持たないため、本情報は
/// <see cref="LogStoreWriteException.IntegratedAuthFailure"/> に載せて発火点
/// （<c>PersistenceWriter</c>——1030 と同じ場所）まで運ぶ。
/// </remarks>
public sealed record IntegratedAuthConnectionFailure(
    IntegratedAuthFailureOrigin Origin,
    string Description,
    string AccountName);
