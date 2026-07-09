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

        public sealed class ReverseDnsOptions
        {
            /// <summary>
            /// 逆引きホスト名表示の有効/無効（既定オン。ADR-0007 決定 4）。
            /// 不正値は §1「縮小側で継続」——外向き DNS クエリを発生させる機能のため、
            /// 不正値では発生しない側（無効）へ倒す。
            /// </summary>
            public string? Enabled { get; set; }
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
