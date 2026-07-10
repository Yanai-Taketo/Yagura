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

    /// <summary>
    /// 再同期バイト数上限の既定値（仮値。実利用で確定する）。
    /// </summary>
    /// <remarks>
    /// PR #169 レビュー指摘 3 へのオーナー決定（2026-07-09）対応: 有効なメッセージが 1 件も
    /// 確定しないまま読み捨てたバイト数（フレーム間 LF/CR のスキップ・上限超過メッセージ本体の
    /// 読み飛ばし・non-transparent-framing の破棄行）がこの上限を超えた接続は、再同期の見込みが
    /// ないゴミデータ送信元と判断して切断する。他社実装の調査（2026-07-09）では、フレーミング
    /// エラー即切断（rsyslog・syslog-ng・Fluent Bit 等）が業界主流であり、Issue #143 の寛容化は
    /// この一次防御を外した状態だった——本上限は寛容化と組み合わせて同等の防御水準を保つ天井
    /// である。既定値 128 KiB は、既定の 1 メッセージ上限（<see cref="DefaultMaxMessageLength"/>
    /// = 64 KiB）を超えたメッセージ 1 通分の読み飛ばし + 余裕を許容しつつ（Issue #143 の
    /// 「上限超過 1 通で接続を全損させない」目的を維持）、調査で得た参考値（Vector の
    /// max_length = 100 KiB、rsyslog の MaxFrameSize 既定 = 200,000 バイト）の間に収まる
    /// 2 の冪として選定した。0 以下で無効化（主にテスト用途）。
    /// </remarks>
    public const int DefaultMaxResyncBytes = 128 * 1024;

    public static readonly TcpFrameDecoderOptions Default = new();

    /// <summary>
    /// 1 メッセージのバイト数上限。既定は <see cref="DefaultMaxMessageLength"/>。
    /// </summary>
    public int MaxMessageLength { get; init; } = DefaultMaxMessageLength;

    /// <summary>
    /// 有効なメッセージが 1 件確定するごとにリセットされる、読み捨てバイト数の上限。
    /// 超過すると <see cref="TcpFrameDecoder.Push"/> が
    /// <see cref="TcpFrameViolationKind.ResyncByteLimitExceeded"/> の
    /// <see cref="TcpFrameSizeExceededException"/> を送出する（呼び出し元は接続を切断する）。
    /// 既定は <see cref="DefaultMaxResyncBytes"/>。0 以下で無効化。
    /// </summary>
    public int MaxResyncBytes { get; init; } = DefaultMaxResyncBytes;

    /// <summary>
    /// <c>true</c> の場合、接続の最初のバイトが数字（octet-counting の判別条件）でなければ
    /// non-transparent-framing への自動判別を行わず、直ちに
    /// <see cref="TcpFrameViolationKind.UnrecoverableCorruption"/> の
    /// <see cref="TcpFrameSizeExceededException"/> を送出する（既定 <c>false</c>——平文 TCP
    /// 受信は RFC 6587 の両方式を許容する従来どおりの動作）。
    /// </summary>
    /// <remarks>
    /// RFC 5425（syslog over TLS。Issue #137）§4.3 は
    /// 「TLS を使う場合、両端は必ず octet-counting を実装しなければならない
    /// （<c>Sender and receiver MUST support the octet-counting…</c>。non-transparent-framing の
    /// 使用は規定しない）」と定める。TLS 受信リスナ（<c>Yagura.Ingestion.Tls.TlsSyslogListener</c>）は
    /// 本フラグを <c>true</c> にして <see cref="TcpFrameDecoder"/> を構成し、この要求を強制する。
    /// </remarks>
    public bool RequireOctetCounting { get; init; }
}
