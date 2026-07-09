namespace Yagura.Ingestion.Tcp;

/// <summary>
/// TCP フレーミングの回復不能な違反を表す——octet-counting の MSG-LEN が再同期不能な形式で
/// 壊れていた（数字以外のバイトの混入・桁数異常・int 範囲超過）、または再同期バイト数上限
/// （<see cref="TcpFrameDecoderOptions.MaxResyncBytes"/>）を超過した。種別は <see cref="Kind"/>。
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
/// 手がかり自体を失う「再同期不能な深刻な破損」と、再同期の見込みがないまま読み捨てが続く
/// 「再同期バイト数上限超過」（PR #169 レビュー指摘 3 へのオーナー決定 2026-07-09）に用途を
/// 絞った。
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
        : this(message, TcpFrameViolationKind.UnrecoverableCorruption)
    {
    }

    public TcpFrameSizeExceededException(string message, TcpFrameViolationKind kind)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>
    /// 違反の種別。呼び出し元（<see cref="TcpSyslogListener"/>）はこの値でログ文言と
    /// 計上先カウンタ（TCP 接続断の内訳）を分ける。
    /// </summary>
    public TcpFrameViolationKind Kind { get; }

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

/// <summary>
/// <see cref="TcpFrameSizeExceededException"/> の違反種別。
/// </summary>
public enum TcpFrameViolationKind
{
    /// <summary>
    /// 再同期不能な深刻な破損（octet-counting の MSG-LEN の桁の途中への数字以外の混入・
    /// 桁数異常・int 範囲超過）。
    /// </summary>
    UnrecoverableCorruption,

    /// <summary>
    /// 再同期バイト数上限（<see cref="TcpFrameDecoderOptions.MaxResyncBytes"/>）の超過——
    /// 有効なメッセージが 1 件も確定しないまま読み捨てたバイト数が上限を超えた
    /// （PR #169 レビュー指摘 3 へのオーナー決定 2026-07-09）。
    /// </summary>
    ResyncByteLimitExceeded,
}
