using Yagura.Ingestion.Tcp;

namespace Yagura.Ingestion.Tls;

/// <summary>
/// <see cref="TlsSyslogListener"/> の構成（syslog over TLS。RFC 5425。TCP 6514。opt-in。Issue #137）。
/// </summary>
/// <remarks>
/// <see cref="TcpSyslogListenerOptions"/> と構造を揃える（security.md §6「参照方式は Web UI の
/// HTTPS と同型」の流儀を、受信段の構成にも一貫させる）。同時接続数上限・1 メッセージ上限・
/// アイドルタイムアウト・再同期バイト数上限・フレーミング進捗タイムアウトの各既定値は
/// <see cref="TcpSyslogListenerOptions"/> の対応する既定値をそのまま引き継ぐ——平文 TCP と別の
/// 値を採用すべき設計上の根拠が無いため（M-14 等の実測確定時は両方を合わせて見直す）。
/// </remarks>
public sealed class TlsSyslogListenerOptions
{
    /// <summary>既定の bind アドレス。TCP と同じ既定（<c>::</c>。Issue #133 の DualMode 方式）。</summary>
    public const string DefaultBindAddress = "::";

    /// <summary>RFC 5425 の標準ポート。</summary>
    public const int DefaultPort = 6514;

    /// <summary>同時接続数上限の既定値（<see cref="TcpSyslogListenerOptions.DefaultMaxConcurrentConnections"/> と同一）。</summary>
    public const int DefaultMaxConcurrentConnections = TcpSyslogListenerOptions.DefaultMaxConcurrentConnections;

    /// <summary>アイドルタイムアウトの既定値（<see cref="TcpSyslogListenerOptions.DefaultIdleTimeout"/> と同一）。</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TcpSyslogListenerOptions.DefaultIdleTimeout;

    /// <summary>フレーミング進捗タイムアウトの既定値（<see cref="TcpSyslogListenerOptions.DefaultFramingProgressTimeout"/> と同一）。</summary>
    public static readonly TimeSpan DefaultFramingProgressTimeout = TcpSyslogListenerOptions.DefaultFramingProgressTimeout;

    /// <summary>bind するアドレス。既定は <see cref="DefaultBindAddress"/>。</summary>
    public string BindAddress { get; init; } = DefaultBindAddress;

    /// <summary>
    /// <see cref="BindAddress"/> が設定で明示指定された値か（<c>false</c> = 既定値のまま）。
    /// 意味づけは <see cref="TcpSyslogListenerOptions.BindAddressIsExplicit"/> と同一。
    /// </summary>
    public bool BindAddressIsExplicit { get; init; }

    /// <summary>bind するポート。既定は <see cref="DefaultPort"/>。テストでは 0 を指定すると OS がポートを採番する。</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>同時接続数上限。既定は <see cref="DefaultMaxConcurrentConnections"/>。</summary>
    public int MaxConcurrentConnections { get; init; } = DefaultMaxConcurrentConnections;

    /// <summary>1 接続・1 メッセージのバイト数上限。既定は <see cref="TcpFrameDecoderOptions.DefaultMaxMessageLength"/>。</summary>
    public int MaxMessageLength { get; init; } = TcpFrameDecoderOptions.DefaultMaxMessageLength;

    /// <summary>接続ごとのアイドルタイムアウト。既定は <see cref="DefaultIdleTimeout"/>。</summary>
    public TimeSpan IdleTimeout { get; init; } = DefaultIdleTimeout;

    /// <summary>再同期バイト数上限。既定は <see cref="TcpFrameDecoderOptions.DefaultMaxResyncBytes"/>。</summary>
    public int MaxResyncBytes { get; init; } = TcpFrameDecoderOptions.DefaultMaxResyncBytes;

    /// <summary>フレーミング進捗タイムアウト。既定は <see cref="DefaultFramingProgressTimeout"/>。</summary>
    public TimeSpan FramingProgressTimeout { get; init; } = DefaultFramingProgressTimeout;
}
