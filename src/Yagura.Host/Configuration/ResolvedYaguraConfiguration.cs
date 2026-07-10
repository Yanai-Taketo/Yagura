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
/// <param name="DefaultRfc3164TimeZone">
/// RFC 3164 TIMESTAMP の既定タイムゾーン（検証済み。Issue #134）。未設定・不正値は
/// <see cref="System.TimeZoneInfo.Utc"/>（現状互換）。送信元付記の TZ（Issue #135）が
/// 取れる場合はそちらが優先され、本値は取れない場合のフォールバックとして使われる。
/// </param>
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
/// <param name="AdminWindowsAuthEnabled">
/// Windows 統合認証（Negotiate）の有効/無効（既定 <c>false</c>。ADR-0010 決定 2）。
/// </param>
/// <param name="AdminWindowsAuthKerberosOnly">
/// Kerberos-only モード（NTLM 無効化 opt-in。既定 <c>false</c>。ADR-0010 決定 2・委任事項 12）。
/// </param>
/// <param name="AdminAppAuthEnabled">
/// アプリ独自 ID/パスワード認証の有効/無効（既定 <c>false</c>。ADR-0010 決定 3）。
/// </param>
/// <param name="AdminAuthRequireForLoopback">
/// loopback アクセスにも認証を課す opt-in（既定 <c>false</c>。ADR-0010 決定 1）。<c>true</c> かつ
/// <see cref="AdminWindowsAuthEnabled"/>/<see cref="AdminAppAuthEnabled"/> がいずれも <c>false</c>
/// の組み合わせは <see cref="YaguraConfigurationLoader.Load"/> が fail-closed で起動を拒否するため、
/// この記録に到達する時点では常に「有効なら認証方式が最低 1 つ構成済み」が成立している。
/// </param>
/// <param name="AdminRemoteBindingEnabled">
/// 管理リスナのリモートバインド解禁（既定 <c>false</c>。ADR-0010 Phase 2 決定 1）。<c>true</c> の
/// 組み合わせは、認証（<see cref="AdminWindowsAuthEnabled"/>/<see cref="AdminAppAuthEnabled"/>の
/// いずれか）と <see cref="AdminHttpsEnabled"/> + 有効な <see cref="AdminHttpsCertificateThumbprint"/>
/// の両方が構成済みであることを <see cref="YaguraConfigurationLoader.Load"/> が fail-closed で
/// 検証済みのため、この記録に到達する時点では常に両条件が成立している（静的な設定検証のみ——
/// 実際の証明書ストア参照の成否は別途 Program 側で確認する）。
/// </param>
/// <param name="AdminHttpsEnabled">
/// 管理リスナのリモート HTTPS 有効/無効（既定 <c>false</c>。ADR-0010 Phase 2 決定 4）。
/// </param>
/// <param name="AdminHttpsCertificateThumbprint">
/// 管理リスナのリモート HTTPS 証明書拇印（正規化済み・大文字 16 進 40 桁。未設定/不正値は
/// <see langword="null"/>）。
/// </param>
/// <param name="AdminHttpsPort">
/// 管理リスナのリモート HTTPS 用ポート（既定 8516。<see cref="AdminHttpPort"/> とは独立）。
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
    TimeZoneInfo DefaultRfc3164TimeZone,
    int HttpPort,
    ViewerPublicAccess ViewerPublicAccess,
    bool ViewerReverseDnsEnabled,
    int AdminHttpPort,
    bool AdminWindowsAuthEnabled,
    bool AdminWindowsAuthKerberosOnly,
    bool AdminAppAuthEnabled,
    bool AdminAuthRequireForLoopback,
    bool AdminRemoteBindingEnabled,
    bool AdminHttpsEnabled,
    string? AdminHttpsCertificateThumbprint,
    int AdminHttpsPort,
    string SqliteFileName,
    bool SpoolEnabled,
    string SpoolDirectory,
    long SpoolQuotaBytes,
    int? RetentionDays,
    TimeOnly RetentionExecutionTimeOfDay,
    StorageProvider StorageProvider,
    string? SqlServerConnectionString)
{
    /// <summary>
    /// <see cref="UdpBindAddress"/> が設定ファイルで明示指定された値か（<c>false</c> = 既定値。
    /// PR #193 レビュー対応）。IPv6 スタックが無効な環境で、既定の <c>::</c> は IPv4 のみへ
    /// 自動縮小して起動を継続するが、明示指定の <c>::</c> は縮小せず起動失敗にする——
    /// この分岐の入力（<c>UdpSyslogListenerOptions.BindAddressIsExplicit</c> 参照）。
    /// </summary>
    public bool UdpBindAddressIsExplicit { get; init; }

    /// <summary>
    /// <see cref="TcpBindAddress"/> が設定ファイルで明示指定された値か（意味づけは
    /// <see cref="UdpBindAddressIsExplicit"/> と同一）。
    /// </summary>
    public bool TcpBindAddressIsExplicit { get; init; }
}
