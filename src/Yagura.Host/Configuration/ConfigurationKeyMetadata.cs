namespace Yagura.Host.Configuration;

/// <summary>
/// 設定キーごとの反映方式（configuration.md §3）の宣言表。
/// </summary>
/// <remarks>
/// <para>
/// <b>属性ではなく静的表を選んだ理由</b>: 反映方式の判定対象は
/// <see cref="YaguraConfigurationOptions"/> の CLR プロパティではなく、
/// <see cref="YaguraConfigurationLoader"/> が既に「JSON キーパス（<c>:</c> 区切り文字列）」
/// という単位で管理している（<c>KnownKeys</c>・<see cref="ConfigurationWarning.Key"/> 等）。
/// C# 属性（<c>[ReloadEffect(...)]</c>）で宣言する場合、POCO のプロパティに付与したうえで
/// リフレクションによりプロパティ階層からキーパス文字列を組み立て直す必要があり、
/// ネストされた <c>Options</c> クラス（<see cref="YaguraConfigurationOptions.IngestionOptions.UdpOptions"/>
/// 等）では入れ子名とキーパスの対応規則を別途定義しなければならない。
/// 一方、静的表はキーパス文字列をそのままキーにでき、<c>KnownKeys</c> と同じ文字列を
/// 追加するだけで済む。**新しい設定キーを追加する PR は、本表と <c>KnownKeys</c> と
/// configuration.md §8 の 3 箇所を同時に更新すること**（configuration.md §8「設定キー追加の規約」）。
/// </para>
/// <para>
/// <b>既存 4 キーの割当</b>（依頼時点の解釈どおり。configuration.md §3・§8 に別段の記載がないため
/// 採用）:
/// <list type="bullet">
/// <item><c>Ingestion:Udp:BindAddress</c> / <c>Ingestion:Udp:Port</c> = リスナ再構成
/// （§8 表「受信」区分の目標値。CF-4 層2——Issue #262——で実装済み: 設定の再読み込みが
/// <c>IngestionPipeline.ReconfigureListenersAsync</c> を呼び、短い瞬断を伴って bind を
/// 張り替える。瞬断区間は受信断のシステムイベントとして記録される）。</item>
/// <item><c>Viewer:HttpPort</c> = サービス再起動（§8 表「UI」区分は「リスナ再構成」を
/// 目標に掲げるが、Kestrel の listen アドレスは <c>WebApplication</c> 構築時に固定され
/// 実行中の付け替え API を持たないため、無瞬断のリスナ再構成が実装されるまでの間は
/// 再起動が実効的な反映方式である。CF-4 確定後に見直す）。</item>
/// <item><c>Storage:SqliteFileName</c> = サービス再起動（§8 表「永続化」区分「組み込み DB
/// の置き場所」は明示的に「置き場所はサービス再起動」と記載）。</item>
/// <item><c>Spool:Enabled</c> / <c>Spool:QuotaBytes</c> = 即時（§8 表「スプール」区分
/// 「有効/無効（opt-out）・置き場所・上限（M-12）| 即時（置き場所のみサービス再起動）」の
/// とおり）。<c>Spool:Directory</c> = サービス再起動（同表の「置き場所のみ」の例外。M4-3）。</item>
/// </list>
/// </para>
/// </remarks>
public static class ConfigurationKeyMetadata
{
    private static readonly IReadOnlyDictionary<string, ConfigurationReloadEffect> ReloadEffectsByKey =
        new Dictionary<string, ConfigurationReloadEffect>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ingestion:Udp:BindAddress"] = ConfigurationReloadEffect.ListenerReconfiguration,
            ["Ingestion:Udp:Port"] = ConfigurationReloadEffect.ListenerReconfiguration,
            // 受信バッファサイズ（M-2）は BindAddress/Port と同じ「受信」区分（§8）に属し、
            // ソケット構築時（bind と同時）に SO_RCVBUF を設定する実装のため、リスナの再構成を
            // 経なければ反映されない。BindAddress/Port と同じ分類とする。
            ["Ingestion:Udp:ReceiveBufferBytes"] = ConfigurationReloadEffect.ListenerReconfiguration,
            // TCP キー(M4-1)は UDP と同じ「受信」区分(§8)。本表への登録が M4-1 で漏れており
            // M5-1 のレビューで追補した(KnownKeys との整合はテストで機械検証される)。
            ["Ingestion:Tcp:BindAddress"] = ConfigurationReloadEffect.ListenerReconfiguration,
            ["Ingestion:Tcp:Port"] = ConfigurationReloadEffect.ListenerReconfiguration,
            // TLS 受信（RFC 5425。opt-in。Issue #137）: §8 表の目標は他の受信系キーと同じ
            // 「リスナ再構成」だが、現時点の実効は Admin:Https:* と同じ理由（証明書ストア参照・
            // 秘密鍵アクセス権付与を含む TLS リスナの構築が Program.cs の起動時処理に固定され、
            // 実行中の付け替え API を持たない）でサービス再起動——CertificateThumbprint は
            // 秘密鍵アクセス権の再付与を要するため特に無瞬断化の優先度が低い。
            ["Ingestion:Tls:Enabled"] = ConfigurationReloadEffect.RestartRequired,
            ["Ingestion:Tls:BindAddress"] = ConfigurationReloadEffect.RestartRequired,
            ["Ingestion:Tls:Port"] = ConfigurationReloadEffect.RestartRequired,
            ["Ingestion:Tls:CertificateThumbprint"] = ConfigurationReloadEffect.RestartRequired,
            // RFC 3164 既定タイムゾーン（Issue #134）はソケットの bind を要さず、解析段
            // （ParsingStage/SyslogParser）の解釈だけに影響するため、リスナ再構成は不要——
            // Retention:* と同じ「即時」を目標とする。現時点の実効は他の即時目標キーと同様、
            // ParsingStage の構築時（DI シングルトン）にのみ値が渡されるため再起動。
            ["Ingestion:Rfc3164:DefaultTimeZone"] = ConfigurationReloadEffect.Immediate,
            // 流量制御(ADR-0002 決定 2。Issue #260)は §8 表「流量制御 | 即時」の目標どおり宣言する
            // (ソケットの bind を要さず、ゲートの差し替え・閾値変更のみで反映できる設計のため)。
            // 現時点の実効は他の即時目標キーと同様、ゲートの構築が起動時(Program の結線)にのみ
            // 行われるためサービス再起動(ライブ再読込 §3 の配線時に目標へ揃える)。
            ["Ingestion:FlowControl:Enabled"] = ConfigurationReloadEffect.Immediate,
            ["Ingestion:FlowControl:MessagesPerSecond"] = ConfigurationReloadEffect.Immediate,
            ["Ingestion:FlowControl:BurstSize"] = ConfigurationReloadEffect.Immediate,
            ["Viewer:HttpPort"] = ConfigurationReloadEffect.RestartRequired,
            // 公開範囲は bind 先の変更を伴うため、Viewer:HttpPort と同じ「リスナ再構成」区分
            // （§8 表「UI」区分の目標）に揃える。現時点の実効はポートと同じくサービス再起動
            // （M6-1 時点は無瞬断リスナ再構成が未実装のため。CF-4 確定後に見直す）。
            ["Viewer:PublicAccess"] = ConfigurationReloadEffect.RestartRequired,
            // 管理ポートはリスナの bind 先自体（loopback 固定）を変えないが、Kestrel の
            // listen アドレス一覧は WebApplication 構築時に固定されるため、他の UI 区分の
            // ポートキーと同じくサービス再起動が現時点の実効（M6-1）。
            ["Admin:HttpPort"] = ConfigurationReloadEffect.RestartRequired,
            ["Storage:SqliteFileName"] = ConfigurationReloadEffect.RestartRequired,
            // provider 切替は database.md §6.1 の切替手順（準備フェーズ→切替本番）による専用の
            // 管理操作であり、通常の設定再読み込み（差分適用）の対象ではない。現時点は
            // ウィザード未実装のため実効はサービス再起動（configuration.md §8 表「永続化」区分
            // 「provider 切替は database.md §6.1 の切替手順による（備考: ウィザード経由のみ）」）。
            ["Storage:Provider"] = ConfigurationReloadEffect.RestartRequired,
            ["Storage:SqlServer:ConnectionString"] = ConfigurationReloadEffect.RestartRequired,
            ["Spool:Enabled"] = ConfigurationReloadEffect.Immediate,
            ["Spool:Directory"] = ConfigurationReloadEffect.RestartRequired,
            ["Spool:QuotaBytes"] = ConfigurationReloadEffect.Immediate,
            // 保持期間(M5-1)は §8 表「保持期間 | 即時」の目標どおり宣言する(Spool:Enabled と
            // 同じ扱い。現時点の実効はスケジューラが起動時にのみ設定を読むため再起動だが、
            // ライブ再読込(§3)配線時に目標へ揃える)。
            ["Retention:Days"] = ConfigurationReloadEffect.Immediate,
            ["Retention:ExecutionTimeOfDay"] = ConfigurationReloadEffect.Immediate,
            // 監査記録の保持期間(SEC-2。Issue #261)は Retention:Days と同じ「即時」を目標とする
            // (ソケットの bind を要さず削除スケジューラの日数のみに影響)。現時点の実効は
            // スケジューラが起動時にのみ設定を読むため再起動(ライブ再読込 §3 配線時に目標へ揃える)。
            ["Audit:RetentionDays"] = ConfigurationReloadEffect.Immediate,
            // 逆引きホスト名表示(ADR-0007)は §8 表の宣言どおり「即時」を目標とする(解決サービスが
            // 呼び出しごとに参照する想定でリスナ再構成を要しない)。現時点の実効は他キー同様、
            // 設定読み込みが起動時のみのため再起動(ライブ再読込 §3 の配線時に目標へ揃える)。
            ["Viewer:ReverseDns:Enabled"] = ConfigurationReloadEffect.Immediate,
            // 管理 UI 認証（ADR-0010 Phase 1）: 認証スキーム構成（AddNegotiate/AddCookie/
            // AddAuthorization）は WebApplicationBuilder 構築時に固定され、実行中の付け替え
            // API を持たないため、Admin:HttpPort と同じくサービス再起動が現時点の実効。
            // 閲覧 UI 認証（ADR-0010 Phase 4 決定 7）: 認証スキーム構成は WebApplicationBuilder 構築時に
            // 固定されるため管理認証キーと同じくサービス再起動が現時点の実効。
            ["Viewer:Authentication:Windows:Enabled"] = ConfigurationReloadEffect.RestartRequired,
            ["Viewer:Authentication:Windows:KerberosOnly"] = ConfigurationReloadEffect.RestartRequired,
            // 注: SEC-9 のグループ一覧（Admin/Viewer:Authentication:Windows:*Groups）は配列キー
            // （KnownArrayKeys）であり、本表（スカラーキーの反映方式表・ChangePlanner の比較対象）とは
            // 別系統で扱う——名 → SID 解決を含め反映は起動時（サービス再起動）に固定であり、ウィザードの
            // 差分適用の対象ではない（手編集のみ。restart-required）。
            ["Admin:Authentication:Windows:Enabled"] = ConfigurationReloadEffect.RestartRequired,
            ["Admin:Authentication:Windows:KerberosOnly"] = ConfigurationReloadEffect.RestartRequired,
            ["Admin:Authentication:App:Enabled"] = ConfigurationReloadEffect.RestartRequired,
            ["Admin:Authentication:RequireForLoopback"] = ConfigurationReloadEffect.RestartRequired,
            // 管理リスナのリモートバインド・HTTPS（ADR-0010 Phase 2 決定 1・4）: bind エントリの
            // 追加・Kestrel の UseHttps 構成はいずれも WebApplicationBuilder 構築時に固定され、
            // 認証キーと同じくサービス再起動が現時点の実効（§8「UI」区分の目標のとおり）。
            ["Admin:RemoteBinding:Enabled"] = ConfigurationReloadEffect.RestartRequired,
            ["Admin:Https:Enabled"] = ConfigurationReloadEffect.RestartRequired,
            ["Admin:Https:CertificateThumbprint"] = ConfigurationReloadEffect.RestartRequired,
            ["Admin:Https:Port"] = ConfigurationReloadEffect.RestartRequired,
        };

    /// <summary>
    /// 指定したキーの反映方式を返す。未登録のキーは呼び出し側の設定漏れ（新キー追加時に
    /// 本表の更新を忘れた等）であるため、既定値へ黙ってフォールバックせず例外にする。
    /// </summary>
    /// <exception cref="KeyNotFoundException">キーが本表に未登録の場合。</exception>
    public static ConfigurationReloadEffect GetReloadEffect(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (ReloadEffectsByKey.TryGetValue(key, out var effect))
        {
            return effect;
        }

        throw new KeyNotFoundException(
            $"設定キー '{key}' の反映方式が ConfigurationKeyMetadata に未登録です。" +
            "新しい設定キーを追加する場合は、本表と YaguraConfigurationLoader の KnownKeys と " +
            "configuration.md §8 を同じ PR で更新してください（configuration.md §8「設定キー追加の規約」）。");
    }

    /// <summary>本表に登録済みのすべてのキー。テスト・網羅性検証用。</summary>
    public static IReadOnlyCollection<string> RegisteredKeys => (IReadOnlyCollection<string>)ReloadEffectsByKey.Keys;
}
