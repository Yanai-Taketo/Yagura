namespace Yagura.Host;

/// <summary>
/// ホスト全体で使う環境変数名・既定値の定数集約。M2 時点は <c>Program.cs</c> に直書きして
/// いたが、M3-1 で設定基盤（<see cref="Yagura.Host.Configuration.YaguraConfigurationLoader"/>）
/// を導入するにあたり、環境変数は「設定ファイル・既定値より優先される上書き手段」として
/// 引き続き維持する（configuration.md §2 の優先順位: 環境変数 &gt; 設定ファイル &gt; 既定値）。
/// </summary>
public static class YaguraHostEnvironment
{
    /// <summary>
    /// データルートを上書きする環境変数名。
    /// </summary>
    public const string DataRootEnvironmentVariable = "YAGURA_DATAROOT";

    /// <summary>
    /// UDP 受信ポートを上書きする環境変数名。<c>0</c> を指定すると OS がポートを採番する
    /// （テスト用。<see cref="Yagura.Ingestion.IngestionPipeline.BoundPort"/> で実ポートを取得できる）。
    /// </summary>
    public const string UdpPortEnvironmentVariable = "YAGURA_UDP_PORT";

    /// <summary>
    /// TCP 受信ポートを上書きする環境変数名（M4-1）。<c>0</c> を指定すると OS がポートを採番する
    /// （テスト用。<see cref="Yagura.Ingestion.IngestionPipeline.TcpBoundPort"/> で実ポートを取得できる）。
    /// </summary>
    public const string TcpPortEnvironmentVariable = "YAGURA_TCP_PORT";

    /// <summary>
    /// 閲覧 HTTP リスナのポートを上書きする環境変数名。<c>0</c> を指定すると OS がポートを
    /// 採番する（テスト用）。
    /// </summary>
    public const string HttpPortEnvironmentVariable = "YAGURA_HTTP_PORT";

    /// <summary>
    /// 閲覧 HTTP リスナの既定ポート（暫定値）。
    /// </summary>
    /// <remarks>
    /// <b>CF-1（configuration.md での既定確定）待ちの暫定値</b>。設計上の既定は「LAN 公開」
    /// だが、閲覧/管理リスナの分離と loopback 束縛の不変条件テストは M6 の作業であり、
    /// それまでは安全側として localhost（127.0.0.1）束縛とする。ポート番号 8514 自体も
    /// 暫定であり、syslog の既定ポート 514 との対応（8 を前置しただけ）以上の根拠はまだない。
    /// </remarks>
    public const int DefaultHttpPort = 8514;
}
