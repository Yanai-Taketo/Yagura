namespace Yagura.Host.Configuration;

/// <summary>
/// 検証・3 分類の適用・環境変数上書きをすべて終えた、起動に使う最終的な設定値。
/// </summary>
/// <remarks>
/// データルートは設定ファイル自体の置き場所を決める入力であり、ファイル内キーではなく
/// 環境変数 <see cref="Yagura.Host.YaguraHostEnvironment.DataRootEnvironmentVariable"/> と
/// 既定値（<c>%ProgramData%\Yagura</c>）のみで解決する（configuration.md §2）。
/// </remarks>
/// <param name="DataRoot">データルートの絶対パス。</param>
/// <param name="UdpBindAddress">UDP 受信リスナの bind アドレス（検証・縮小適用済み）。</param>
/// <param name="UdpPort">UDP 受信リスナのポート（検証済み）。</param>
/// <param name="UdpReceiveBufferBytes">
/// UDP 受信ソケットの受信バッファサイズ（バイト。M-2。検証済み）。既定は
/// <see cref="Yagura.Ingestion.Udp.UdpSyslogListenerOptions.DefaultReceiveBufferBytes"/>。
/// </param>
/// <param name="TcpBindAddress">TCP 受信リスナの bind アドレス（検証・縮小適用済み。M4-1）。</param>
/// <param name="TcpPort">TCP 受信リスナのポート（検証済み。M4-1）。</param>
/// <param name="HttpPort">閲覧 HTTP リスナのポート（検証済み）。</param>
/// <param name="ViewerPublicAccess">
/// 閲覧リスナの公開範囲（既定 <see cref="Configuration.ViewerPublicAccess.Lan"/>。M6-1）。
/// 不正値は <see cref="Configuration.ViewerPublicAccess.LocalhostOnly"/> へ縮小済み。
/// </param>
/// <param name="ViewerReverseDnsEnabled">
/// 逆引き（PTR）ホスト名表示の有効/無効（既定オン。ADR-0007 決定 4）。不正値は縮小側
/// （無効 = DNS クエリを発しない）へ適用済み。
/// </param>
/// <param name="AdminHttpPort">
/// 管理 HTTP リスナのポート（検証済み。M6-1）。bind 先は常に <c>127.0.0.1</c> / <c>::1</c>
/// 固定（設定で変更不可——configuration.md §1 の不変条件）。
/// </param>
/// <param name="SqliteFileName">データルート配下の SQLite ファイル名（検証済み）。</param>
/// <param name="SpoolEnabled">スプールの有効/無効（既定 <c>true</c>。opt-out。M4-3）。</param>
/// <param name="SpoolDirectory">スプールディレクトリの絶対パス（既定はデータルート配下。M4-3）。</param>
/// <param name="SpoolQuotaBytes">スプールのディスク使用量上限（バイト。M-12 実測確定待ちの暫定既定値。M4-3）。</param>
/// <param name="RetentionDays">
/// 保持期間（日数）。<c>null</c> は「削除しない」（database.md DB-1 確定前の暫定既定。M5-1）。
/// </param>
/// <param name="RetentionExecutionTimeOfDay">保持期間削除の定期実行の開始時刻（サーバローカル時刻。M5-1）。</param>
/// <param name="StorageProvider">
/// 永続化 provider の選択（M5-3。既定 <see cref="Configuration.StorageProvider.Sqlite"/>）。
/// <c>Storage:SqlServer:ConnectionString</c> が未設定のまま <c>sqlserver</c> が指定された場合、
/// この値は <see cref="Configuration.StorageProvider.Sqlite"/> へ縮小済み（<see cref="YaguraConfigurationLoader"/>
/// の設計判断。§1「既定値で継続」の適用——最終報告参照）。
/// </param>
/// <param name="SqlServerConnectionString">
/// SQL Server provider 選択時の接続文字列（<see cref="StorageProvider"/> が
/// <see cref="Configuration.StorageProvider.SqlServer"/> のときのみ非 null）。
/// </param>
public sealed record ResolvedYaguraConfiguration(
    string DataRoot,
    string UdpBindAddress,
    int UdpPort,
    int UdpReceiveBufferBytes,
    string TcpBindAddress,
    int TcpPort,
    int HttpPort,
    ViewerPublicAccess ViewerPublicAccess,
    bool ViewerReverseDnsEnabled,
    int AdminHttpPort,
    string SqliteFileName,
    bool SpoolEnabled,
    string SpoolDirectory,
    long SpoolQuotaBytes,
    int? RetentionDays,
    TimeOnly RetentionExecutionTimeOfDay,
    StorageProvider StorageProvider,
    string? SqlServerConnectionString);
