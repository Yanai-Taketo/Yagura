namespace Yagura.Ingestion.Tcp;

/// <summary>
/// <see cref="TcpFrameDecoder"/> の構成。
/// </summary>
public sealed class TcpFrameDecoderOptions
{
    /// <summary>
    /// 1 接続・1 メッセージのバイト数上限の既定値（暫定定数）。
    /// </summary>
    /// <remarks>
    /// architecture.md に規定はない（M4 実装時の判断）。巨大な octet-count を騙る送信元や、
    /// LF を送らず流し込み続ける送信元への防御として設ける。RFC 3164 の実用上限
    /// （1024 オクテット）・RFC 5424 の拡張長を踏まえ、実運用の構造化データを含む長文メッセージ
    /// にも十分な余裕を持たせつつ、無制限のメモリ確保を防ぐ値として 64 KiB を暫定採用する。
    /// 実測確定は architecture.md §9 の実測待ち一覧に準ずる運用（本件は同表に未掲載のため、
    /// 本 PR の最終報告で「暫定定数」として明示する）。
    /// </remarks>
    public const int DefaultMaxMessageLength = 64 * 1024;

    public static readonly TcpFrameDecoderOptions Default = new();

    /// <summary>
    /// 1 メッセージのバイト数上限。既定は <see cref="DefaultMaxMessageLength"/>。
    /// </summary>
    public int MaxMessageLength { get; init; } = DefaultMaxMessageLength;
}
