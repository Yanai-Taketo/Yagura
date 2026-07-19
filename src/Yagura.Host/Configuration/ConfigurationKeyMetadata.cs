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
            // （KnownArrayKeys）であり、下の ArrayReloadEffectsByKey で宣言する（本表はスカラー専用）。
            // 反映は名 → SID 解決を含め起動時に固定（restart-required）であることは変わらないが、
            // 2026-07-19 以降は ChangePlanner の比較対象であり、手編集での変更が「再起動待ち」として
            // 表示される（それ以前は変更が検出されず無音だった。ADR-0017 委任 9）。
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
            // メール通知（ADR-0017。opt-in）は §8 表の宣言どおり「即時」とする——ソケットの
            // bind も Kestrel の再構成も要さず、SMTP 接続は送信のたびに張る（常設接続を持たない）
            // ため、次回送信から新しい値が効く（ADR-0017 決定 9）。現時点の実効は送信側の
            // 実装（Issue #350 第 2 段）が無く ImmediateConfigurationApplier が未配線のため
            // 再起動待ちに落ちる——送信側の実装と同じ PR で目標へ揃える。
            // 注: 宛先一覧（Notification:Email:To）は配列キー（KnownArrayKeys）であり、
            // 本表（スカラーキーの表）には載せない——Windows 認証のグループ一覧と同じ扱い。
            // ただし反映方式は他のメールキーと同じ即時である（ADR-0017 委任 9）。
            ["Notification:Email:Enabled"] = ConfigurationReloadEffect.Immediate,
            ["Notification:Email:From"] = ConfigurationReloadEffect.Immediate,
            ["Notification:Email:Smtp:Host"] = ConfigurationReloadEffect.Immediate,
            ["Notification:Email:Smtp:Port"] = ConfigurationReloadEffect.Immediate,
            ["Notification:Email:Smtp:Security"] = ConfigurationReloadEffect.Immediate,
            ["Notification:Email:Smtp:Username"] = ConfigurationReloadEffect.Immediate,
            ["Notification:Email:Smtp:Password"] = ConfigurationReloadEffect.Immediate,
        };

    /// <summary>
    /// 配列キー（<see cref="YaguraConfigurationLoader.KnownArrayKeys"/>）の反映方式。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>スカラー表と分けている理由</b>: 配列キーは .NET 構成システム上
    /// <c>&lt;key&gt;:0</c>・<c>&lt;key&gt;:1</c> … のインデックス付きリーフに展開されるため
    /// <see cref="YaguraConfigurationLoader.KnownKeys"/>（スカラーのリーフ集合）には現れない。
    /// 両者を 1 つの表に混ぜると「スカラー表 ⇔ KnownKeys の双方向一致」というテストの
    /// 不変条件が壊れる——別表にして、それぞれ対応する集合と突き合わせる。
    /// </para>
    /// <para>
    /// <b>2026-07-19 の追加（ADR-0017 委任 9。Issue #350）</b>: 従来、配列キーは
    /// <see cref="ConfigurationChangePlanner"/> の比較対象ですらなかった。結果として
    /// <b>手編集で宛先やグループ一覧だけを変えて再読み込みしても、反映もされなければ
    /// 「再起動待ち」としても現れない</b>——configuration.md §3 が約束する「未反映のまま残る
    /// 項目の明示」から漏れる無音の穴だった。メール通知の宛先（即時反映が目標）を扱うには
    /// この穴を塞ぐ必要があり、同じ性質を持つグループ一覧 3 キーも併せて登録する。
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, ConfigurationReloadEffect> ArrayReloadEffectsByKey =
        new Dictionary<string, ConfigurationReloadEffect>(StringComparer.OrdinalIgnoreCase)
        {
            // SEC-9 のグループ一覧（ADR-0010 決定 5・7）: 名 → SID 解決を含め反映は起動時に固定。
            // 「即時が目標だが未配線」ではなく、設計上ここは再起動である。
            ["Admin:Authentication:Windows:AdminGroups"] = ConfigurationReloadEffect.RestartRequired,
            ["Viewer:Authentication:Windows:ViewerGroups"] = ConfigurationReloadEffect.RestartRequired,
            ["Viewer:Authentication:Windows:AdminGroups"] = ConfigurationReloadEffect.RestartRequired,
            // メール通知の宛先（ADR-0017 決定 9）。他のメールキーと同じく即時——送信のたびに
            // SMTP 接続を張るため、次回送信から新しい宛先が効く。
            ["Notification:Email:To"] = ConfigurationReloadEffect.Immediate,
        };

    /// <summary>
    /// 指定したキーの反映方式を返す。未登録のキーは呼び出し側の設定漏れ（新キー追加時に
    /// 本表の更新を忘れた等）であるため、既定値へ黙ってフォールバックせず例外にする。
    /// </summary>
    /// <exception cref="KeyNotFoundException">キーが本表に未登録の場合。</exception>
    public static ConfigurationReloadEffect GetReloadEffect(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (ReloadEffectsByKey.TryGetValue(key, out var effect)
            || ArrayReloadEffectsByKey.TryGetValue(key, out effect))
        {
            return effect;
        }

        throw new KeyNotFoundException(
            $"設定キー '{key}' の反映方式が ConfigurationKeyMetadata に未登録です。" +
            "新しい設定キーを追加する場合は、本表と YaguraConfigurationLoader の KnownKeys と " +
            "configuration.md §8 を同じ PR で更新してください（configuration.md §8「設定キー追加の規約」）。");
    }

    /// <summary>
    /// スカラーキーの表に登録済みのすべてのキー。テスト・網羅性検証用
    /// （<see cref="YaguraConfigurationLoader.KnownKeys"/> と双方向に一致すること）。
    /// </summary>
    public static IReadOnlyCollection<string> RegisteredKeys => (IReadOnlyCollection<string>)ReloadEffectsByKey.Keys;

    /// <summary>
    /// 配列キーの表に登録済みのすべてのキー。テスト・網羅性検証用
    /// （<see cref="YaguraConfigurationLoader.KnownArrayKeys"/> と双方向に一致すること）。
    /// </summary>
    public static IReadOnlyCollection<string> RegisteredArrayKeys =>
        (IReadOnlyCollection<string>)ArrayReloadEffectsByKey.Keys;
}
