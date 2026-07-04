namespace Yagura.Ingestion.Udp;

/// <summary>
/// <see cref="UdpSyslogListener"/> の構成。
/// </summary>
/// <remarks>
/// 本格的な設定基盤（JSON 設定ファイル・ウィザード反映）は M3 で行う。M2 時点は
/// この POCO をコードから直接組み立てる。
/// </remarks>
public sealed class UdpSyslogListenerOptions
{
    /// <summary>
    /// 既定の bind アドレス（すべてのインターフェース）。configuration.md での既定確定前の暫定値。
    /// </summary>
    public const string DefaultBindAddress = "0.0.0.0";

    /// <summary>
    /// syslog の既定 UDP ポート（RFC 5426）。
    /// </summary>
    public const int DefaultPort = 514;

    /// <summary>
    /// bind するアドレス。既定は <see cref="DefaultBindAddress"/>（0.0.0.0 = すべてのインターフェース）。
    /// </summary>
    public string BindAddress { get; init; } = DefaultBindAddress;

    /// <summary>
    /// bind するポート。既定は <see cref="DefaultPort"/>。
    /// テストでは 0 を指定すると OS がポートを採番する
    /// （<see cref="UdpSyslogListener.BoundPort"/> で実際に束縛されたポートを取得できる）。
    /// </summary>
    public int Port { get; init; } = DefaultPort;
}
