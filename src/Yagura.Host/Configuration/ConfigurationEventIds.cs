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

    /// <summary>
    /// 管理リスナのリモートバインド（<c>Admin:RemoteBinding:Enabled</c>）が有効なのに、
    /// 認証（Windows 統合認証・アプリ独自認証のいずれか）と HTTPS（<c>Admin:Https:Enabled</c> +
    /// 有効な証明書拇印）の少なくとも一方が構成されていない設定の fail-closed 拒否
    /// （ADR-0010 Phase 2 決定 1・4）。レベルはエラー（<see cref="AdminAuthenticationFailClosedStartupRejected"/>
    /// と同じ「起動失敗に直結する重大事象」区分）。
    /// </summary>
    /// <remarks>
    /// 採番の経緯: 1011 は main 側で ADR-0010 Phase 1（PR #217）に割り当て済みのため、
    /// 本イベントは additive-only 規約に従い次の 1012 を採る。
    /// </remarks>
    public static readonly EventId AdminRemoteBindingFailClosedStartupRejected =
        new(1012, "AdminRemoteBindingFailClosedStartupRejected");

    /// <summary>
    /// 管理リスナのリモートバインドが有効かつ静的な設定検証（fail-closed。上記）は通過したが、
    /// 実際の証明書ストア参照（<c>Admin:Https:CertificateThumbprint</c>）が失敗した（証明書が
    /// 見つからない・秘密鍵にアクセスできない・既に期限切れ等）場合の起動時警告
    /// （ADR-0010 Phase 2 決定 4）。**起動は中止しない**——configuration.md §4.1「指定した bind 先が
    /// 使用できない場合...そのリスナは開かずに縮小側で継続する」と同じ縮小側の扱いを、リモート
    /// HTTPS の bind エントリ 1 本に対して適用する（管理リスナ全体・loopback 面は影響を受けない。
    /// ADR-0010 Phase 2 決定 4「loopback 経由の管理リスナは HTTPS の対象外のまま残る」）。
    /// レベルは警告（機能停止を伴わない縮退——リモート面のみ開けないだけで loopback 経由の
    /// 復旧は引き続き可能なため）。
    /// </summary>
    public static readonly EventId AdminHttpsCertificateUnavailableAtStartup =
        new(1013, "AdminHttpsCertificateUnavailableAtStartup");

    /// <summary>
    /// TLS 受信（<c>Ingestion:Tls:Enabled</c>。RFC 5425。opt-in。Issue #137）が有効なのに、
    /// 実際の証明書ストア参照（<c>Ingestion:Tls:CertificateThumbprint</c>）が失敗した（拇印が
    /// 未設定・不正形式・証明書が見つからない・秘密鍵にアクセスできない）場合の起動時警告
    /// （security.md §6）。<b>起動は中止しない</b>——TLS 受信の bind エントリのみを開かずに
    /// 縮小継続する（<see cref="AdminHttpsCertificateUnavailableAtStartup"/> と同型の扱い）。
    /// 平文 UDP/TCP 受信は一切影響を受けない（ADR-0004 決定 3。TLS の障害は平文経路に影響しない）。
    /// レベルは警告——受信全体の機能停止ではなく TLS 面のみの縮退のため。
    /// </summary>
    /// <remarks>
    /// 採番の経緯: 1015 まで既存実装（ADR-0010 Phase 2）が使用済みのため、additive-only 規約に
    /// 従い次の 1016 を採る。
    /// </remarks>
    public static readonly EventId IngestionTlsCertificateUnavailableAtStartup =
        new(1016, "IngestionTlsCertificateUnavailableAtStartup");

    /// <summary>
    /// 設定の再読み込み（configuration.md §3。CF-4 層1。Issue #262）で、変更キーの一部が
    /// 反映にサービス再起動（または層2 のリスナ再構成）を要し、**未反映のまま残っている**
    /// 場合の警告。§3「変更に『サービス再起動』の項目が含まれる場合、未反映のまま残る項目を
    /// 再読み込みの結果として UI とイベントログに明示する」の実装。レベルは警告——
    /// 「設定した = 反映された」という前提が静かに崩れている状態を放置させないため。
    /// 再読み込みの実行自体の証跡は監査事象 2016（情報）が担う（本 ID は未反映の残存のみ）。
    /// </summary>
    /// <remarks>採番の経緯: 1017〜1019 は ActiveNotificationEventIds 側が使用済みのため 1020。</remarks>
    public static readonly EventId ConfigurationReloadPendingRestart =
        new(1020, "ConfigurationReloadPendingRestart");

    /// <summary>
    /// 設定の再読み込みが検証失敗（configuration.md §1 の「起動失敗」分類の不正値）により
    /// 拒否された場合の警告。**実行中の構成は旧設定のまま継続する**——起動時は fail-fast
    /// （起動失敗）だが、稼働中は「受信を止めない」を優先して適用だけを拒否する非対称が仕様
    /// （Issue #262 の設計判断）。レベルは警告。
    /// </summary>
    public static readonly EventId ConfigurationReloadRejected =
        new(1021, "ConfigurationReloadRejected");

    /// <summary>
    /// 起動時に受信リスナの一部（または全部）が環境要因（ポート競合・アドレス未確立等）で
    /// bind できず、開けたリスナのみで縮小継続している場合の警告（configuration.md §4.1。
    /// Issue #291——#141 原子的起動の反転。2026-07-16 オーナー裁定）。開けなかったリスナは
    /// CF-6 の定期再試行（仮値 30 秒間隔）が受信再開を試み続け、成功すると受信断区間
    /// （<c>downtime.listener-bind-retry</c>）が記録される。レベルは警告——受信面の一部が
    /// 開いていない縮退状態を放置させないため。
    /// </summary>
    public static readonly EventId ListenerBindFailedDegradedStartup =
        new(1022, "ListenerBindFailedDegradedStartup");

    /// <summary>
    /// リスナの実ポートと Yagura 名前空間のファイアウォール規則の不一致（CF-2。
    /// configuration.md §4.3。Issue #265）。起動時とリスナ再構成の適用時に検出する。
    /// ファイアウォールでの drop はアプリのカウンタにも OS のソケット統計にも現れない
    /// 観測の完全な死角であり、「ポートを変えたのに届かない」を無音で固定化させないための警告。
    /// レベルは警告。
    /// </summary>
    public static readonly EventId FirewallRuleMismatch =
        new(1023, "FirewallRuleMismatch");
}
