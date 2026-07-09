namespace Yagura.Ingestion.Tcp;

/// <summary>
/// octet-counting の MSG-LEN が再同期不能な形式で壊れていたことを表す（数字以外のバイトの
/// 混入・桁数異常・int 範囲超過）。
/// </summary>
/// <remarks>
/// <para>
/// <b>Issue #143 での再スコープ</b>: 当初は 1 メッセージのバイト数上限
/// （<see cref="TcpFrameDecoderOptions.MaxMessageLength"/>）超過もこの例外で表していたが、
/// 上限超過は「MSG-LEN 自体は正しく読み取れている」「1 行の長さが分かっている」ため再同期
/// 可能であり、接続を切断するほどの安全側判断は過剰と判断した。現在はサイズ上限超過（octet-
/// counting・non-transparent-framing のいずれも）は<b>当該メッセージのみを破棄して接続を維持する
/// </b>（<see cref="TcpFrameDecoder.OversizedMessagesDiscardedCount"/> で計上。呼び出し元の
/// カウンタ・ログへの反映は <see cref="TcpSyslogListener"/> が担う）。本例外は、フレーム境界の
/// 手がかり自体を失う「再同期不能な深刻な破損」に用途を絞った。
/// </para>
/// <para>
/// 呼び出し元（<see cref="TcpSyslogListener"/>）はこの例外を「安全側」として扱い、接続を
/// 切断する（切断時点の読みかけデータは database.md §2.1 の Incomplete として Q1 へ流す。
/// 例外送出までに確定していた正常メッセージは <see cref="CompletedMessages"/> から取り出して
/// 切断前に Q1 へ流す）。
/// </para>
/// </remarks>
public sealed class TcpFrameSizeExceededException : Exception
{
    public TcpFrameSizeExceededException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 例外送出までに、同一の <see cref="TcpFrameDecoder.Push"/> 呼び出し内で境界が確定していた
    /// 正常メッセージの一覧（PR #169 レビュー指摘への対応）。呼び出し元
    /// （<see cref="TcpSyslogListener"/>）は接続を切断する前にこれらを Q1 へ流す——例外とともに
    /// 確定済みメッセージまで黙って失うと、Q1 未到達かつどのカウンタにも現れない無計上の
    /// 喪失経路になり、「損失は必ずどれかのカウンタに計上される」（architecture.md §3.1・§4.1）
    /// の原則を破るため。
    /// </summary>
    public IReadOnlyList<byte[]> CompletedMessages { get; internal set; } = Array.Empty<byte[]>();
}
