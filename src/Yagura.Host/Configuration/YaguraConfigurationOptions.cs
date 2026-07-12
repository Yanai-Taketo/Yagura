namespace Yagura.Host.Configuration;

/// <summary>
/// 設定ファイル（既定 <c>yagura.json</c>。<see cref="YaguraConfigurationLoader"/> 参照）の
/// JSON 構造にそのままバインドする POCO。configuration.md §8 の設定スキーマ一覧のうち、
/// 現時点で実在する項目（受信・UI・永続化の一部・スプール）のみをモデル化する
/// （additive-only の起点。§8 の他区分——流量制御・保持期間・通知——は当該機能の
/// 実装時に追加する）。
/// </summary>
/// <remarks>
/// 本クラスは「ファイルに書かれていた生の値」を保持する検証前の中間表現である。
/// 値の妥当性検証・3 分類（起動失敗 / 既定値継続 / 縮小継続）の適用・環境変数による
/// 上書きは <see cref="YaguraConfigurationLoader"/> が行い、検証済みの最終値は
/// <see cref="ResolvedYaguraConfiguration"/> にまとめる。
/// 各プロパティは <c>string?</c> 等の緩い型で受ける（JSON の型不一致・欠落を
/// バインド段階で例外にせず、後段の検証で「不正値」として一様に扱うため）。
/// </remarks>
public sealed class YaguraConfigurationOptions
{
    /// <summary>設定ファイル内の JSON セクション名（.NET 構成システムの規約に合わせルート直下）。</summary>
    public const string SectionName = "";

    /// <summary>§8「受信」区分。</summary>
    public IngestionOptions? Ingestion { get; set; }

    /// <summary>§8「UI」区分（閲覧ポート・公開範囲。M6-1。HTTPS 証明書等は M6 以降で追加）。</summary>
    public ViewerOptions? Viewer { get; set; }

    /// <summary>§8「UI」区分のうち管理リスナ（M6-1。Issue #51）。</summary>
    public AdminOptions? Admin { get; set; }

    /// <summary>§8「永続化」区分のうち組み込み DB の置き場所。データルート自体は §2 参照。</summary>
    public StorageOptions? Storage { get; set; }

    /// <summary>§8「スプール」区分（M4-3）。</summary>
    public SpoolOptions? Spool { get; set; }

    /// <summary>§8「保持期間」区分（M5-1。database.md §3）。</summary>
    public RetentionOptions? Retention { get; set; }

    public sealed class IngestionOptions
    {
        /// <summary>UDP 受信リスナの設定。</summary>
        public UdpOptions? Udp { get; set; }

        /// <summary>TCP 受信リスナの設定（M4-1）。</summary>
        public TcpOptions? Tcp { get; set; }

        /// <summary>TLS 受信リスナの設定（syslog over TLS。RFC 5425。opt-in。Issue #137）。</summary>
        public TlsOptions? Tls { get; set; }

        /// <summary>RFC 3164 TIMESTAMP 解釈の設定（Issue #134・#135）。</summary>
        public Rfc3164Options? Rfc3164 { get; set; }

        public sealed class UdpOptions
        {
            /// <summary>bind するアドレス（文字列のまま保持し、検証段で <see cref="System.Net.IPAddress"/> 等へ変換する）。</summary>
            public string? BindAddress { get; set; }

            /// <summary>bind するポート。JSON の数値以外（文字列・範囲外）も受けられるよう <c>string?</c> で保持する。</summary>
            public string? Port { get; set; }

            /// <summary>
            /// UDP 受信ソケットの受信バッファサイズ（<c>SO_RCVBUF</c>。バイト単位。M-2）。
            /// 既定は <see cref="Yagura.Ingestion.Udp.UdpSyslogListenerOptions.DefaultReceiveBufferBytes"/>。
            /// 不正値は §1「既定値で継続」——受信の成立に不可欠なキーではない。
            /// </summary>
            public string? ReceiveBufferBytes { get; set; }
        }

        public sealed class TcpOptions
        {
            /// <summary>bind するアドレス（文字列のまま保持し、検証段で <see cref="System.Net.IPAddress"/> 等へ変換する）。</summary>
            public string? BindAddress { get; set; }

            /// <summary>bind するポート。JSON の数値以外（文字列・範囲外）も受けられるよう <c>string?</c> で保持する。</summary>
            public string? Port { get; set; }
        }

        public sealed class TlsOptions
        {
            /// <summary>
            /// TLS 受信を有効化する（既定 <c>false</c>。opt-in。security.md §6）。<c>true</c> でも
            /// <see cref="CertificateThumbprint"/> が解決できなければ、TLS 受信の bind エントリのみを
            /// 開かずに縮小継続する（configuration.md §4.1 と同型。平文 UDP/TCP には影響しない）。
            /// </summary>
            public string? Enabled { get; set; }

            /// <summary>bind するアドレス（文字列のまま保持。既定は TCP と同じ <c>::</c>）。</summary>
            public string? BindAddress { get; set; }

            /// <summary>bind するポート（既定 6514。RFC 5425 の標準ポート）。</summary>
            public string? Port { get; set; }

            /// <summary>
            /// Windows 証明書ストア（ローカルコンピューター・<c>My</c>）内の証明書を選択する拇印
            /// （SHA-1、40 桁の 16 進表記。configuration.md §6・<c>Admin:Https:CertificateThumbprint</c>
            /// と同型——参照方式を共有するが設定キーは独立させる。security.md §6）。
            /// </summary>
            public string? CertificateThumbprint { get; set; }
        }

        public sealed class Rfc3164Options
        {
            /// <summary>
            /// RFC 3164 TIMESTAMP（年・タイムゾーンを持たない）の解釈に使う既定タイムゾーン
            /// （Issue #134）。値は Windows タイムゾーン ID（例: <c>Tokyo Standard Time</c>）または
            /// IANA タイムゾーン ID（例: <c>Asia/Tokyo</c>）——.NET 6 以降は
            /// <see cref="System.TimeZoneInfo.FindSystemTimeZoneById(string)"/> が Windows 上でも
            /// 両方の ID 体系を受理する（<c>Yagura.Ingestion.Tests</c> の
            /// <c>SyslogParserRfc3164TimeZoneTests</c> で実機検証済み）。既定（未設定）は UTC
            /// （現状互換）。TIMESTAMP に送信元付記の TZ（Issue #135）が取れた場合はそちらが優先され、
            /// 本設定は取れない場合にのみ適用される。不正値は §1「既定値で継続」——UTC へ
            /// フォールバックし警告する（受信の成立に不可欠なキーではない）。
            /// </summary>
            public string? DefaultTimeZone { get; set; }
        }
    }

    public sealed class ViewerOptions
    {
        /// <summary>閲覧 HTTP リスナのポート。</summary>
        public string? HttpPort { get; set; }

        /// <summary>
        /// 閲覧リスナの公開範囲（<c>Lan</c> 既定 / <c>LocalhostOnly</c>。M6-1）。
        /// 不正値は §1「縮小側で継続」——<c>LocalhostOnly</c>（より狭い側）へ縮小する。
        /// </summary>
        public string? PublicAccess { get; set; }

        /// <summary>逆引き（PTR）ホスト名表示の設定（ADR-0007）。</summary>
        public ReverseDnsOptions? ReverseDns { get; set; }

        /// <summary>
        /// 閲覧 UI 認証（ADR-0010 Phase 4。決定 7。opt-in。既定は現状維持——認証なし・LAN 公開）。
        /// 有効化すると閲覧リスナ（8514）到達に Windows 統合認証 + AD グループ判定を要する
        /// （<see cref="AuthenticationOptions"/>）。既定（未設定）では体験は一切変わらない。
        /// </summary>
        public AuthenticationOptions? Authentication { get; set; }

        public sealed class ReverseDnsOptions
        {
            /// <summary>
            /// 逆引きホスト名表示の有効/無効（既定オン。ADR-0007 決定 4）。
            /// 不正値は §1「縮小側で継続」——外向き DNS クエリを発生させる機能のため、
            /// 不正値では発生しない側（無効）へ倒す。
            /// </summary>
            public string? Enabled { get; set; }
        }

        public sealed class AuthenticationOptions
        {
            /// <summary>
            /// 閲覧 UI の Windows 統合認証（Negotiate）+ AD グループマッピング（ADR-0010 決定 7・
            /// SEC-9）。閲覧の主経路。アプリ独自 ID/パスワードは管理役割専用のため閲覧固有の設定は
            /// 持たない（管理 ⊇ 閲覧で到達する。決定 5・7）。
            /// </summary>
            public WindowsOptions? Windows { get; set; }

            public sealed class WindowsOptions
            {
                /// <summary>閲覧 UI の Windows 統合認証を有効化する（既定 <c>false</c>）。</summary>
                public string? Enabled { get; set; }

                /// <summary>
                /// Kerberos-only モード（NTLM 無効化 opt-in。管理側と同型。ADR-0010 決定 2・委任事項 12。
                /// 既定 <c>false</c>）。閲覧リスナ経由の Negotiate にのみ作用する（管理リスナの
                /// <c>Admin:Authentication:Windows:KerberosOnly</c> とは独立）。
                /// </summary>
                public string? KerberosOnly { get; set; }

                /// <summary>
                /// 「閲覧」役割にマップする AD グループ（SEC-9。security.md §3）。各要素は
                /// グループ名（<c>DOMAIN\Group</c>）または SID（<c>S-1-...</c>）——起動時に名を SID へ
                /// 解決してキャッシュする。所属判定は <see cref="System.Security.Principal.WindowsIdentity"/> の
                /// 推移的グループ SID（ネストは OS が展開済み——追加 LDAP 不要）と設定グループ SID 集合の
                /// 照合で行う。
                /// </summary>
                public List<string>? ViewerGroups { get; set; }

                /// <summary>
                /// 閲覧リスナ経由のログインで「管理」役割にマップする AD グループ（SEC-9）。ここに所属する
                /// 利用者は管理セッション（<c>admin_session</c>）を得るため管理 ⊇ 閲覧で閲覧できる
                /// （かつ管理リスナ（8515）へも到達できる——Cookie は host スコープ）。閲覧リスナ（8514）の
                /// 画面には管理機能を表示しない（左ナビの管理導線は管理リスナ帰属時のみ）。
                /// </summary>
                public List<string>? AdminGroups { get; set; }
            }
        }
    }

    public sealed class AdminOptions
    {
        /// <summary>
        /// 管理 HTTP リスナのポート（M6-1）。bind 先（127.0.0.1 / ::1）を変える設定キーは
        /// 設けない——管理リスナは設定がどう壊れていても loopback 以外へ束縛されない
        /// （configuration.md §1 の不変条件・security.md §1 L-4）。
        /// </summary>
        public string? HttpPort { get; set; }

        /// <summary>
        /// 管理 UI 認証（ADR-0010 Phase 1。opt-in。既定は現状維持——認証なし）。
        /// </summary>
        public AuthenticationOptions? Authentication { get; set; }

        /// <summary>
        /// 管理リスナのリモートバインド解禁（ADR-0010 Phase 2 決定 1。opt-in。既定 <c>false</c>——
        /// 現状（loopback 束縛のみ）を維持する）。有効化には認証（<see cref="AuthenticationOptions"/>
        /// のいずれか）と HTTPS（<see cref="HttpsOptions"/>）の両方が構成済みであることを要する
        /// （fail-closed 不変条件。<see cref="YaguraConfigurationLoader"/> 参照）。
        /// </summary>
        public RemoteBindingOptions? RemoteBinding { get; set; }

        /// <summary>
        /// 管理リスナのリモートバインド面（<see cref="RemoteBindingOptions"/>）用 HTTPS 証明書設定
        /// （ADR-0010 Phase 2 決定 4）。設定キーは閲覧リスナの HTTPS（configuration.md §6）とは
        /// 独立させる——暗黙の連動をしない（同一証明書の流用は両方のキーに指定することで実現する）。
        /// </summary>
        public HttpsOptions? Https { get; set; }

        public sealed class RemoteBindingOptions
        {
            /// <summary>
            /// リモートバインドを有効化する（既定 <c>false</c>）。<c>true</c> かつ認証・HTTPS の
            /// いずれかが未構成の組み合わせは fail-closed で起動を拒否する（ADR-0010 Phase 2 決定 1）。
            /// </summary>
            public string? Enabled { get; set; }
        }

        public sealed class HttpsOptions
        {
            /// <summary>
            /// 管理リスナのリモート HTTPS を有効化する（既定 <c>false</c>）。
            /// </summary>
            public string? Enabled { get; set; }

            /// <summary>
            /// Windows 証明書ストア（ローカルコンピューター・<c>My</c>）内の証明書を選択する拇印
            /// （SHA-1、40 桁の 16 進表記。configuration.md §6 と同型——PFX パス + パスワード方式は
            /// 採らない）。空白・区切り文字は正規化して比較する。
            /// </summary>
            public string? CertificateThumbprint { get; set; }

            /// <summary>
            /// 管理リスナのリモート HTTPS 用ポート（既定 8516）。<see cref="AdminOptions.HttpPort"/>
            /// （既定 8515。loopback・平文 HTTP 専用のまま）とは独立のポートとする——同一ポートで
            /// loopback は平文・remote は HTTPS という 2 通りの扱いを共存させることは、OS の
            /// bind 制約（ワイルドカード bind と特定アドレス bind は同一ポートで共存できない）
            /// および ADR-0010 Phase 2 決定 4「loopback 経由の管理リスナは HTTPS の対象外のまま残る」
            /// （証明書事故時も loopback からの復旧を維持する）の両方から、別ポートでの提供を要する。
            /// </summary>
            public string? Port { get; set; }
        }

        public sealed class AuthenticationOptions
        {
            /// <summary>Windows 統合認証（Negotiate）の設定（ADR-0010 決定 2）。</summary>
            public WindowsOptions? Windows { get; set; }

            /// <summary>アプリ独自 ID/パスワード認証の設定（ADR-0010 決定 3）。</summary>
            public AppOptions? App { get; set; }

            /// <summary>
            /// loopback アクセスにも認証を課す opt-in（ADR-0010 決定 1）。既定 <c>false</c>
            /// （既定は現状どおり loopback 無認証を維持——最終復旧経路としての価値を守る）。
            /// <c>true</c> かつ <see cref="Windows"/>/<see cref="App"/> がいずれも無効な組み合わせは
            /// fail-closed で起動を拒否する（<see cref="YaguraConfigurationLoader"/> 参照）。
            /// </summary>
            public string? RequireForLoopback { get; set; }

            public sealed class WindowsOptions
            {
                /// <summary>Windows 統合認証（Negotiate）を有効化する（既定 <c>false</c>）。</summary>
                public string? Enabled { get; set; }

                /// <summary>
                /// Kerberos-only モード（NTLM 無効化 opt-in。ADR-0010 決定 2・委任事項 12。既定 <c>false</c>）。
                /// </summary>
                public string? KerberosOnly { get; set; }

                /// <summary>
                /// 「管理」役割にマップする AD グループ（SEC-9。ADR-0010 決定 5・委任事項 8。security.md §3）。
                /// 既定の <c>BUILTIN\Administrators</c>（well-known SID <c>S-1-5-32-544</c>）判定に<b>加えて</b>、
                /// ここに指定したグループの所属者も管理者として認可する（544 判定を置き換えず追加する）。
                /// 各要素はグループ名（<c>DOMAIN\Group</c>）または SID（<c>S-1-...</c>）——起動時に名を SID へ
                /// 解決してキャッシュする。ネストは <see cref="System.Security.Principal.WindowsIdentity"/> の
                /// 推移的グループ SID（OS 展開済み——追加 LDAP 不要）で解決する。
                /// </summary>
                public List<string>? AdminGroups { get; set; }
            }

            public sealed class AppOptions
            {
                /// <summary>アプリ独自 ID/パスワード認証を有効化する（既定 <c>false</c>）。</summary>
                public string? Enabled { get; set; }
            }
        }
    }

    public sealed class StorageOptions
    {
        /// <summary>
        /// データルート配下の SQLite ファイル名（既定 <c>yagura.db</c>）。パス区切りを含む値は
        /// 不正として既定値へフォールバックする（データルート脱出を防ぐ。§1「既定値で継続」）。
        /// </summary>
        public string? SqliteFileName { get; set; }

        /// <summary>
        /// 永続化 provider の選択（<c>sqlite</c> 既定 / <c>sqlserver</c>）。M5-3。
        /// 不正値は §1「既定値で継続」——<c>sqlite</c> へフォールバックし警告する。
        /// </summary>
        public string? Provider { get; set; }

        /// <summary>SQL Server provider 選択時の接続設定（M5-3）。</summary>
        public SqlServerOptions? SqlServer { get; set; }
    }

    public sealed class SqlServerOptions
    {
        /// <summary>
        /// SQL Server への接続文字列。<c>Storage:Provider = sqlserver</c> のとき必須
        /// （configuration.md §2 の DPAPI 保護対象。復号は本 Issue の範囲外——挿入点のみ）。
        /// </summary>
        public string? ConnectionString { get; set; }
    }

    public sealed class SpoolOptions
    {
        /// <summary>
        /// スプールの有効/無効（既定 <c>true</c>。opt-out。configuration.md §8「スプール」区分）。
        /// </summary>
        public string? Enabled { get; set; }

        /// <summary>
        /// スプールディレクトリの絶対パス（既定はデータルート配下。configuration.md §2）。
        /// </summary>
        public string? Directory { get; set; }

        /// <summary>
        /// ディスク使用量上限（バイト）。既定は <see cref="Yagura.Storage.Spool.SpoolConstants.DefaultQuotaBytes"/>
        /// （M-12 実測確定待ちの暫定値）。
        /// </summary>
        public string? QuotaBytes { get; set; }
    }

    public sealed class RetentionOptions
    {
        /// <summary>
        /// 保持期間（日数）。JSON キーは <c>Retention:Days</c>。<c>null</c>/未設定は「削除しない」
        /// （database.md DB-1 の既定値確定前の暫定既定。本 Issue の設計判断——ゼロ設定ファーストランで
        /// ディスク枯渇の自動復旧経路を持たない代わりに、意図せぬ自動削除で調査対象のログを失う
        /// 事故を避ける安全側）。
        /// </summary>
        public string? Days { get; set; }

        /// <summary>
        /// 定期実行の開始時刻（サーバのローカル時刻。<c>HH:mm</c> 形式）。未設定時は既定値
        /// （<see cref="Yagura.Host.Retention.RetentionSchedulerOptions.DefaultExecutionTimeOfDay"/>）を使う。
        /// </summary>
        public string? ExecutionTimeOfDay { get; set; }
    }
}
