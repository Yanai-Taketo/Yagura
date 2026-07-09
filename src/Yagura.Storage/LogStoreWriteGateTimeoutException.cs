namespace Yagura.Storage;

/// <summary>
/// <see cref="LogStoreWriteGate"/> の取得が指定時間内に完了しなかったことを表す（Issue #151）。
/// </summary>
/// <remarks>
/// DB 呼び出し自体のタイムアウト（<see cref="LogStoreWriteException"/>）とは意図的に区別する
/// ——本例外の原因は「他の書き込み経路（保持期間削除・drain・ライブ書き込みのいずれか）が
/// ゲートを保持中」であり、provider 自体の遅延・ハングとは診断上分けて扱えるようにする
/// （呼び出し元のログメッセージで「ゲート待ちタイムアウト」と「DB 書き込みタイムアウト」を
/// 区別できることが、偽の「速度不足」誤診断を防ぐ——database.md §3 が名指しするリスク）。
/// </remarks>
public sealed class LogStoreWriteGateTimeoutException : TimeoutException
{
    public LogStoreWriteGateTimeoutException(TimeSpan timeout)
        : base($"書き込みゲートの取得が {timeout} 以内に完了しませんでした。")
    {
        Timeout = timeout;
    }

    /// <summary>取得を試みた際のタイムアウト値。</summary>
    public TimeSpan Timeout { get; }
}
