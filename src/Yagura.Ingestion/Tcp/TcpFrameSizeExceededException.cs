namespace Yagura.Ingestion.Tcp;

/// <summary>
/// 1 接続・1 メッセージのバイト数上限（<see cref="TcpFrameDecoderOptions.MaxMessageLength"/>）
/// を超えた、または octet-counting の MSG-LEN が不正な形式だったことを表す。
/// </summary>
/// <remarks>
/// 呼び出し元（<see cref="TcpSyslogListener"/>）はこの例外を「安全側」として扱い、接続を
/// 切断する（M4 依頼の判断——巨大な octet-count や LF が来ない stream への防御。切断時点の
/// 読みかけデータは database.md §2.1 の Incomplete として Q1 へ流す）。
/// </remarks>
public sealed class TcpFrameSizeExceededException : Exception
{
    public TcpFrameSizeExceededException(string message)
        : base(message)
    {
    }
}
