namespace Yagura.Storage;

/// <summary>
/// ログ永続化 provider の抽象（database.md §1.2 provider 契約）。
/// </summary>
/// <remarks>
/// <para>
/// M2 時点の最小サブセット。ここに定義する操作は database.md §1.2 の契約 1・2・4
/// （スキーマ管理・バッチ挿入・対話的検索）に対応する。契約 3・5・6
/// （失敗の分類報告・保持期間の削除・統計）は M5 で拡張する——本インターフェースへの
/// メソッド追加として行い、既存メソッドの意味論は変更しない（database.md §7 の
/// 「契約への操作追加」の互換規則に従う）。
/// </para>
/// <para>
/// <b>書き込みは単一 writer が呼び出す契約とする</b>（database.md §4）。
/// <see cref="WriteBatchAsync"/> の呼び出しは、呼び出し側（永続化段）が直列化する
/// ことを前提とし、本インターフェースの実装は複数呼び出し元からの並行呼び出しに
/// 対する排他制御を提供しない。保持期間削除（M5 で追加）も同じ書き込み経路に
/// 直列化される想定である。
/// </para>
/// </remarks>
public interface ILogStore
{
    /// <summary>
    /// スキーマを新規作成する。<b>冪等</b>——既にスキーマが存在する場合は何もしない
    /// （database.md §1.2 契約 1 の最小形。版間移行・権限不足の区別可能な報告は M5 で拡張する）。
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// ログレコードをバッチ書き込みする（database.md §1.2 契約 2、architecture.md §7 要求①）。
    /// </summary>
    /// <param name="records">書き込むレコードの集合。<see cref="LogRecord.Id"/> は無視され、provider が採番する。</param>
    Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default);

    /// <summary>
    /// 最新 <paramref name="limit"/> 件を <see cref="LogRecord.ReceivedAt"/> 降順で返す
    /// （database.md §1.2 契約 4 の形。射影 + 結果上限 + タイムアウトを必須引数とする）。
    /// </summary>
    /// <param name="limit">返却件数の上限。</param>
    /// <param name="timeout">クエリの実行時間上限。超過時は例外を送出する。</param>
    Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
