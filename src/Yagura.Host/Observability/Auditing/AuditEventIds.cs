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

    /// <summary>アプリ独自認証アカウントのロックアウト発生（ADR-0010 決定 6）。レベルは警告。</summary>
    public static readonly EventId AdminAccountLockedOut = new(3005, "AdminAccountLockedOut");

    /// <summary>アプリ独自認証アカウントのロックアウト解除（ADR-0010 決定 6）。レベルは警告。</summary>
    public static readonly EventId AdminAccountLockoutReleased = new(3006, "AdminAccountLockoutReleased");
}
