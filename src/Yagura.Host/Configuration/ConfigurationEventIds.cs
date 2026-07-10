using Microsoft.Extensions.Logging;

namespace Yagura.Host.Configuration;

/// <summary>
/// 設定検証段の起動失敗（1000 番台。security.md §4.3「運用警告」区画。ADR-0010 決定 6）で使う
/// イベント ID。既存の起動失敗（受信ポート不正等）は <see cref="ConfigurationValidationException"/>
/// を送出するのみで個別 ID を持たない（従来どおり EventId 0 で記録される）——本クラスは
/// ADR-0010 が名指しした「重大事象として 1000 番台のエラーレベル相当で扱う」対象にのみ、
/// additive に専用 ID を割り当てる。
/// </summary>
public static class ConfigurationEventIds
{
    /// <summary>
    /// loopback 認証 opt-in（<c>Admin:Authentication:RequireForLoopback</c>）が有効なのに
    /// 認証方式（Windows 統合認証・アプリ独自認証）が一つも有効に構成されていない設定の
    /// fail-closed 拒否（ADR-0010 決定 1・委任事項 5）。レベルはエラー
    /// （起動失敗に直結する重大事象——他の 1000 番台の「警告」より一段強い。security.md §4.3
    /// のレベル割当方針「機能停止を伴う事象はエラー」の適用）。
    /// </summary>
    /// <remarks>
    /// 採番の経緯: 1009 は main 側で Issue #152（スプールの定期自己検証失敗）に割り当て済み、
    /// 1010 は PR #211（スプール自己検証タイムアウトのバックログ起因の区別）が使用し先に
    /// マージされる見込みのため（additive-only 規約——一度公開した ID の意味は変えない。
    /// 採番は PR #217 レビューで裁定済み）、本イベントは 1011 を採る。
    /// </remarks>
    public static readonly EventId AdminAuthenticationFailClosedStartupRejected =
        new(1011, "AdminAuthenticationFailClosedStartupRejected");
}
