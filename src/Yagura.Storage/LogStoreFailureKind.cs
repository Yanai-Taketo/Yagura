namespace Yagura.Storage;

/// <summary>
/// 書き込み失敗の 3 分類（database.md §1.2 契約 3「失敗の分類報告」）。
/// </summary>
/// <remarks>
/// 応答しないハング（provider が例外もタイムアウトも返さず沈黙する状態）は本分類の対象外——
/// 呼び出し側（永続化段）のタイムアウトが打ち切る（architecture.md §3.2.1）。
/// </remarks>
public enum LogStoreFailureKind
{
    /// <summary>
    /// 一時障害。再試行に意味がある（一時的なディスク I/O エラー・ロック競合等）。
    /// 呼び出し側はスプールへの退避・再試行で扱う（現行どおり）。
    /// </summary>
    Transient,

    /// <summary>
    /// 恒久障害。設定・スキーマ・権限の問題等、再試行しても解消しない。
    /// 呼び出し側は警告を強め、連続発火時の警告連発を抑制する。
    /// </summary>
    Permanent,

    /// <summary>
    /// 容量枯渇。保存領域の上限に達した状態で、削除により回復し得る
    /// （database.md §3・§4・§5.3。保持期間削除の前倒し実行で自走復旧を試みる）。
    /// </summary>
    CapacityExhausted,
}
