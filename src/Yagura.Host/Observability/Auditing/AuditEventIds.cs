using Microsoft.Extensions.Logging;

namespace Yagura.Host.Observability.Auditing;

/// <summary>
/// Windows イベントログの監査系イベント ID 採番表（security.md §4.3。SEC-5。
/// 2000 番台 = 管理操作の監査（レベル: 情報）/ 3000 番台 = 拒否・セキュリティ事象（レベル: 警告）。
/// M6-2（Issue #52）で 3001 を、M8-4（Issue #71）で 2001〜2004・3002 を、
/// ADR-0008 実装で 2005 を採番）。
/// </summary>
/// <remarks>
/// <para>
/// <b>additive-only の凍結対象</b>: security.md §4.3「一度公開した ID の意味とレベルは変えない」
/// に従う。本クラスへ新しい ID を追加する場合は、必ず security.md §4.3 の ID 表へ同じ PR で
/// 追記すること（意味の変更・転用は行わない）。
/// </para>
/// <para>
/// <b>例外記録（v0.1 期の訂正。issue #237）</b>: 3003（<see cref="WindowsAuthenticationHandshakeFailed"/>）は
/// 当初「握手失敗」と「認証成功だが管理者権限なし」の両方を含めていたが、issue #237 で後者を
/// <see cref="AdminAuthorizationDenied"/>（3008）へ分離し、3003 を握手失敗のみへ narrow した
/// （名実の乖離を早期に正す意図的な意味変更。v0.1 期・オーナー承認のうえの例外。security.md §4.3 参照）。
/// v1.0 での ID 凍結以降は同種の意味変更を行わない。
/// </para>
/// </remarks>
public static class AuditEventIds
{
    // ---- 2000 番台: 管理操作の監査（レベル: 情報） ----

    /// <summary>設定変更の適用（初期セットアップウィザードの設定生成を含む）。レベルは情報。</summary>
    public static readonly EventId ConfigurationSaved = new(2001, "ConfigurationSaved");

    /// <summary>本番昇格の準備フェーズにおける SQL Server 接続検証の実施。レベルは情報。</summary>
    public static readonly EventId PromotionConnectionValidated = new(2002, "PromotionConnectionValidated");

    /// <summary>本番昇格の切替実行。レベルは情報。</summary>
    public static readonly EventId PromotionExecuted = new(2003, "PromotionExecuted");

    /// <summary>circuit の個別切断（管理操作）。レベルは情報。</summary>
    public static readonly EventId CircuitDisconnected = new(2004, "CircuitDisconnected");

    /// <summary>フォワーダ配布キットの生成（ADR-0008）。レベルは情報。</summary>
    public static readonly EventId ForwarderKitGenerated = new(2005, "ForwarderKitGenerated");

    /// <summary>管理 UI 認証設定の変更（ADR-0010 決定 1・3）。レベルは情報。</summary>
    public static readonly EventId AdminAuthenticationConfigured = new(2006, "AdminAuthenticationConfigured");

    /// <summary>アプリ独自認証の管理者アカウントの作成・パスワード変更（ADR-0010 決定 3）。レベルは情報。</summary>
    public static readonly EventId AdminAccountCreated = new(2007, "AdminAccountCreated");

    /// <summary>
    /// 管理 UI へのサインイン成功（Windows 統合認証・アプリ独自認証の両方。ADR-0010 決定 6
    /// 「誰が」欄の実効化の起点）。レベルは情報。
    /// </summary>
    public static readonly EventId AdminLoginSucceeded = new(2008, "AdminLoginSucceeded");

    /// <summary>
    /// 管理リスナ HTTPS 証明書の秘密鍵読み取り権限をサービスアカウントへ付与した
    /// （ADR-0010 Phase 2 決定 4。configuration.md §6 の既存方式——閲覧 UI の HTTPS 証明書選択時の
    /// 付与——と同型。付与対象・拇印を記録し秘密鍵そのものは記録しない）。レベルは情報。
    /// </summary>
    public static readonly EventId AdminHttpsCertificatePrivateKeyAccessGranted =
        new(2009, "AdminHttpsCertificatePrivateKeyAccessGranted");

    /// <summary>
    /// TLS 受信（RFC 5425。opt-in）証明書の秘密鍵読み取り権限をサービスアカウントへ付与した
    /// （security.md §6。<see cref="AdminHttpsCertificatePrivateKeyAccessGranted"/> と同型。
    /// 付与対象・拇印を記録し秘密鍵そのものは記録しない。Issue #137）。レベルは情報。
    /// </summary>
    public static readonly EventId IngestionTlsCertificatePrivateKeyAccessGranted =
        new(2010, "IngestionTlsCertificatePrivateKeyAccessGranted");

    /// <summary>
    /// 管理リスナのリモートバインド（<c>Admin:RemoteBinding:Enabled</c>）の有効化・無効化
    /// （ADR-0012 決定 7。認証設定変更（2006）と別 ID——「機の公開」を他のフラグ変更に
    /// 埋もれさせない）。レベルは情報。
    /// </summary>
    public static readonly EventId AdminRemoteBindingConfigured =
        new(2011, "AdminRemoteBindingConfigured");

    /// <summary>
    /// 管理リスナのリモート HTTPS 設定（<c>Admin:Https:Enabled</c>・証明書拇印・ポート）の変更
    /// （ADR-0012 決定 7）。レベルは情報。
    /// </summary>
    public static readonly EventId AdminHttpsCertificateConfigured =
        new(2012, "AdminHttpsCertificateConfigured");

    // ---- 3000 番台: 拒否・セキュリティ事象（レベル: 警告） ----

    /// <summary>
    /// 閲覧リスナに到達した管理系要求の拒否（security.md §1 L-3b）。レベルは警告。
    /// </summary>
    public static readonly EventId ViewerListenerAdminRequestRejected = new(3001, "ViewerListenerAdminRequestRejected");

    /// <summary>同一サイト以外からの circuit 確立試行の拒否（origin 検証。security.md §2.1）。レベルは警告。</summary>
    public static readonly EventId CircuitOriginRejected = new(3002, "CircuitOriginRejected");

    /// <summary>Windows 統合認証（Negotiate）のハンドシェイク失敗・拒否（ADR-0010 決定 6）。レベルは警告。</summary>
    public static readonly EventId WindowsAuthenticationHandshakeFailed = new(3003, "WindowsAuthenticationHandshakeFailed");

    /// <summary>アプリ独自認証のログイン失敗（ADR-0010 決定 6）。レベルは警告。</summary>
    public static readonly EventId AppAuthenticationLoginFailed = new(3004, "AppAuthenticationLoginFailed");

    /// <summary>
    /// アプリ独自認証アカウントのロックアウト発生（ADR-0010 決定 6）。レベルは警告。
    /// <b>凍結（ADR-0011 決定 9）</b>: 三層防御（バックオフ + IP レート制限 + グローバルトークン
    /// バケット）の採用以降、本 ID は発火しない。意味・レベルは変更しない。
    /// </summary>
    public static readonly EventId AdminAccountLockedOut = new(3005, "AdminAccountLockedOut");

    /// <summary>
    /// アプリ独自認証のアカウント単位バックオフが cap（上限遅延）に到達（ADR-0011 決定 3・9）。
    /// レベルは警告。
    /// </summary>
    public static readonly EventId AdminAuthBackoffCapReached = new(3006, "AdminAuthBackoffCapReached");

    /// <summary>
    /// IP レート制限またはグローバルトークンバケットによる拒否（ADR-0011 決定 2・4・5.1・9）。
    /// レベルは警告。
    /// </summary>
    public static readonly EventId AdminAuthRateLimited = new(3007, "AdminAuthRateLimited");

    /// <summary>
    /// 認証成功後の認可拒否（管理者権限なし。ADR-0010 決定 6・issue #237）。レベルは警告。
    /// プロトコル握手失敗（<see cref="WindowsAuthenticationHandshakeFailed"/>=3003）とは別事象として分離する。
    /// </summary>
    public static readonly EventId AdminAuthorizationDenied = new(3008, "AdminAuthorizationDenied");
}
