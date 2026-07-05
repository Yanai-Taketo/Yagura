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
    /// 管理 HTTP リスナのポートを上書きする環境変数名（M6-1。Issue #51）。<c>0</c> を指定すると
    /// OS がポートを採番する（テスト用。E2E テストは <c>YAGURA_UDP_PORT</c> 等と同じ流儀で
    /// 衝突なくポートを取得する）。
    /// </summary>
    public const string AdminPortEnvironmentVariable = "YAGURA_ADMIN_PORT";

    /// <summary>
    /// 閲覧 HTTP リスナの既定ポート。
    /// </summary>
    /// <remarks>
    /// <b>CF-1 確定値（Issue #51。2026-07-05 オーナー決定）</b>: 閲覧 8514 / 管理 8515。
    /// 選定理由・IANA 衝突調査は configuration.md §4.2 と Issue #51 の決定記録を参照。
    /// 既定の bind 先は LAN 公開（configuration.md §4.2「閲覧リスナは既定で LAN に公開する」）
    /// ——bind アドレス自体は <see cref="Yagura.Host.Configuration.YaguraConfigurationLoader"/>
    /// が <c>Viewer:PublicAccess</c> の解決結果から決める。
    /// </remarks>
    public const int DefaultHttpPort = 8514;

    /// <summary>
    /// 管理 HTTP リスナの既定ポート（M6-1。CF-1 確定値）。
    /// </summary>
    /// <remarks>
    /// 管理リスナは <c>127.0.0.1</c> と <c>::1</c> の両系統のみへ常に束縛する
    /// （configuration.md §1 の縮小側原則・security.md §1 L-4 の不変条件）。
    /// bind 先を変える設定キーは設けない——ポート番号のみ <c>Admin:HttpPort</c> で変更できる。
    /// </remarks>
    public const int DefaultAdminPort = 8515;
}
