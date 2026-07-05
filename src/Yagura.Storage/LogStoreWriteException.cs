namespace Yagura.Storage;

/// <summary>
/// provider の書き込み系操作（<see cref="ILogStore.WriteBatchAsync"/> 等）が失敗したことを
/// <see cref="LogStoreFailureKind"/> の 3 分類付きで報告する例外（database.md §1.2 契約 3）。
/// </summary>
/// <remarks>
/// provider 実装（<c>SqliteLogStore</c> 等）は、下位ライブラリの例外（<c>SqliteException</c> 等）を
/// 分類して本例外へ変換してから送出する。呼び出し側（永続化段・ホスト）は本例外の
/// <see cref="FailureKind"/> のみを見て分岐すればよく、provider 固有の例外型を知る必要がない。
/// </remarks>
public sealed class LogStoreWriteException : Exception
{
    /// <summary>
    /// 失敗の分類。
    /// </summary>
    public LogStoreFailureKind FailureKind { get; }

    public LogStoreWriteException(LogStoreFailureKind failureKind, string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    public LogStoreWriteException(LogStoreFailureKind failureKind, string message, Exception innerException)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }
}
