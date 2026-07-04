namespace Yagura.Ingestion.Tcp;

/// <summary>
/// <see cref="TcpSyslogListener"/> の構成。
/// </summary>
/// <remarks>
/// <see cref="Yagura.Ingestion.Udp.UdpSyslogListenerOptions"/> と構造を揃える（M4-1 依頼
/// 「既存コード把握: UDP の構造に揃えること」）。
/// </remarks>
public sealed class TcpSyslogListenerOptions
{
    /// <summary>既定の bind アドレス（すべてのインターフェース）。UDP と同じ既定に揃える。</summary>
    public const string DefaultBindAddress = "0.0.0.0";

    /// <summary>syslog の既定 TCP ポート（RFC 6587 は UDP と同じ 514 番を前提とする実装が一般的）。</summary>
    public const int DefaultPort = 514;

    /// <summary>
    /// 同時接続数上限の既定値（暫定定数）。
    /// </summary>
    /// <remarks>
    /// architecture.md §9 M-14「TCP 同時接続数上限の既定値」は実測 + 想定送信元数を踏まえて
    /// 確定するとされており未確定。本実装は「読み取り停止中も接続は保持される」（§3.1）ため
    /// 資源の有限化として暫定値 256 を採用する（一般的な中小規模の syslog 送信元数を大きく
    /// 上回る余裕を見た値。M-14 の実測確定時に差し替える）。
    /// </remarks>
    public const int DefaultMaxConcurrentConnections = 256;

    /// <summary>bind するアドレス。既定は <see cref="DefaultBindAddress"/>。</summary>
    public string BindAddress { get; init; } = DefaultBindAddress;

    /// <summary>
    /// bind するポート。既定は <see cref="DefaultPort"/>。
    /// テストでは 0 を指定すると OS がポートを採番する（<see cref="TcpSyslogListener.BoundPort"/> 参照）。
    /// </summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>同時接続数上限。既定は <see cref="DefaultMaxConcurrentConnections"/>。</summary>
    public int MaxConcurrentConnections { get; init; } = DefaultMaxConcurrentConnections;

    /// <summary>1 接続・1 メッセージのバイト数上限。既定は <see cref="TcpFrameDecoderOptions.DefaultMaxMessageLength"/>。</summary>
    public int MaxMessageLength { get; init; } = TcpFrameDecoderOptions.DefaultMaxMessageLength;
}
