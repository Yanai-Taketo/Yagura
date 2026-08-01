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
    public static readonly EventId AdminAuthenticationFailClosedStartupRejected =
        new(1011, "AdminAuthenticationFailClosedStartupRejected");

    /// <summary>
    /// 管理リスナのリモートバインド（<c>Admin:RemoteBinding:Enabled</c>）が有効なのに、
    /// 認証（Windows 統合認証・アプリ独自認証のいずれか）と HTTPS（<c>Admin:Https:Enabled</c> +
    /// 有効な証明書拇印）の少なくとも一方が構成されていない設定の fail-closed 拒否
    /// （ADR-0010 Phase 2 決定 1・4）。レベルはエラー（<see cref="AdminAuthenticationFailClosedStartupRejected"/>
    /// と同じ「起動失敗に直結する重大事象」区分）。
    /// </summary>
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
    /// TLS 受信（<c>Ingestion:Tls:Enabled</c>。RFC 5425。opt-in）が有効なのに、
    /// 実際の証明書ストア参照（<c>Ingestion:Tls:CertificateThumbprint</c>）が失敗した（拇印が
    /// 未設定・不正形式・証明書が見つからない・秘密鍵にアクセスできない）場合の起動時警告
    /// （security.md §6）。<b>起動は中止しない</b>——TLS 受信の bind エントリのみを開かずに
    /// 縮小継続する（<see cref="AdminHttpsCertificateUnavailableAtStartup"/> と同型の扱い）。
    /// 平文 UDP/TCP 受信は一切影響を受けない（ADR-0004 決定 3。TLS の障害は平文経路に影響しない）。
    /// レベルは警告——受信全体の機能停止ではなく TLS 面のみの縮退のため。
    /// </summary>
    public static readonly EventId IngestionTlsCertificateUnavailableAtStartup =
        new(1016, "IngestionTlsCertificateUnavailableAtStartup");

    /// <summary>
    /// 設定の再読み込み（configuration.md §3。CF-4 層1）で、変更キーの一部が
    /// 反映にサービス再起動（または層2 のリスナ再構成）を要し、**未反映のまま残っている**
    /// 場合の警告。§3「変更に『サービス再起動』の項目が含まれる場合、未反映のまま残る項目を
    /// 再読み込みの結果として UI とイベントログに明示する」の実装。レベルは警告——
    /// 「設定した = 反映された」という前提が静かに崩れている状態を放置させないため。
    /// 再読み込みの実行自体の証跡は監査事象 2016（情報）が担う（本 ID は未反映の残存のみ）。
    /// </summary>
    /// <remarks>1017〜1019 は ActiveNotificationEventIds 側で使用済みのため、本イベントは 1020 を採る。</remarks>
    public static readonly EventId ConfigurationReloadPendingRestart =
        new(1020, "ConfigurationReloadPendingRestart");

    /// <summary>
    /// 設定の再読み込みが検証失敗（configuration.md §1 の「起動失敗」分類の不正値）により
    /// 拒否された場合の警告。**実行中の構成は旧設定のまま継続する**——起動時は fail-fast
    /// （起動失敗）だが、稼働中は「受信を止めない」を優先して適用だけを拒否する非対称が仕様
    /// （設計判断）。レベルは警告。
    /// </summary>
    public static readonly EventId ConfigurationReloadRejected =
        new(1021, "ConfigurationReloadRejected");

    /// <summary>
    /// 起動時に受信リスナの一部（または全部）が環境要因（ポート競合・アドレス未確立等）で
    /// bind できず、開けたリスナのみで縮小継続している場合の警告（configuration.md §4.1）。開けなかったリスナは
    /// CF-6 の定期再試行（仮値 30 秒間隔）が受信再開を試み続け、成功すると受信断区間
    /// （<c>downtime.listener-bind-retry</c>）が記録される。レベルは警告——受信面の一部が
    /// 開いていない縮退状態を放置させないため。
    /// </summary>
    public static readonly EventId ListenerBindFailedDegradedStartup =
        new(1022, "ListenerBindFailedDegradedStartup");

    /// <summary>
    /// リスナの実ポートと Yagura 名前空間のファイアウォール規則の不一致（CF-2。
    /// configuration.md §4.3）。起動時とリスナ再構成の適用時に検出する。
    /// ファイアウォールでの drop はアプリのカウンタにも OS のソケット統計にも現れない
    /// 観測の完全な死角であり、「ポートを変えたのに届かない」を無音で固定化させないための警告。
    /// レベルは警告。
    /// </summary>
    public static readonly EventId FirewallRuleMismatch =
        new(1023, "FirewallRuleMismatch");

    /// <summary>
    /// 設定ファイルをファイル全体として解釈できず（構文エラー・文字化け・重複キー）、起動に失敗した
    /// （configuration.md §1。security.md §4.3）。レベルはエラー——機能停止を伴うため。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>なぜ起動失敗なのか</b>: キー単位の縮退に分解できないため「何が既定へ落ちたか」を提示できず、
    /// 「可視化された縮退」（architecture.md §1.2）の系に載せられないためである。可視化できる縮退
    /// （単一キーの不正値・接続文字列不備による SQLite 縮小など）は従来どおり継続する。
    /// </para>
    /// <para>
    /// <b>本イベントが唯一の通知経路になる</b>: 起動に失敗すると閲覧 UI も上がらないため、Yagura 自身で
    /// この状態を検知することは構造的にできない（イベントログ以外のログを集めるための製品であるため）。
    /// 加えてサービスの失敗時ポリシーは再起動 3 回で打ち切られ、以後は停止したままとなる。利用者には
    /// サービス死活の外形監視を案内する（configuration.md §10 CF-D8）。
    /// </para>
    /// <para>
    /// 1019 は <see cref="Yagura.Host.Observability.ActiveNotification.ActiveNotificationEventIds.AdminAuthFailureDefenseEscalated"/>
    /// （ADR-0011 決定 6）が実装済みで使用中であり、1020〜1023 も使用済みのため、
    /// 本イベントは 1000 番台の次の空き番号 **1024** を採る。
    /// </para>
    /// </remarks>
    public static readonly EventId ConfigurationFileUnreadableStartupFailed =
        new(1024, "ConfigurationFileUnreadableStartupFailed");

    /// <summary>
    /// フォワーダ MSI アップロードの fail-closed 設定を拒否して起動を中止（ADR-0021 決定 1。
    /// 前提条件は「認証方式が最低 1 つ有効」のみ——無認証 loopback からの到達遮断は
    /// アップロード操作単位の専用認可ポリシーが担う）。
    /// <c>Admin:ForwarderKit:MsiUpload:Enabled</c> が有効なのに、前提条件（管理 UI 認証の
    /// いずれかが有効 = サインインの手段が存在する）が満たされていない設定の起動時拒否。
    /// 1011/1012 と同型の「起動失敗」分類。エラーメッセージには復旧に必要な
    /// 具体の設定キーと値（<c>Admin:ForwarderKit:MsiUpload:Enabled を false に</c>）を含める
    /// （手編集復旧の場面では UI の誘導が使えないため）。
    /// 採番: 1000 番台の次の空き番号 1032（1025〜1029 はメール通知・途絶検知、1030〜1031 は
    /// Persistence 側が使用済み）。ID の意味（アップロード前提条件の fail-closed）は
    /// 判定条件が変わっても不変（additive-only 規約——意味の変更ではなく条件の縮小）。
    /// </summary>
    public static readonly EventId ForwarderMsiUploadFailClosedStartupRejected =
        new(1032, "ForwarderMsiUploadFailClosedStartupRejected");

    /// <summary>
    /// 閲覧 UI の HTTPS（<c>Viewer:Https:Enabled</c>。ADR-0022。opt-in）が構成されているのに
    /// 閲覧リスナを HTTPS で開けない場合の起動時警告（縮小継続——起動は中止しない）。対象は
    /// ①証明書ストア参照の失敗（証明書が見つからない・秘密鍵にアクセスできない・既に期限切れ）と
    /// ②設定の静的な不成立（拇印が未設定・形式不正、または <c>Enabled</c> が真偽値として不正
    /// かつ拇印設定済み——<see cref="ViewerHttpsMode.SuppressListener"/>）の両方。
    /// <b>閲覧リスナ（既定 8514）は平文 HTTP では開かず、このリスナだけを開かない</b>
    /// （ADR-0022 決定 2——受信・解析・保存・スプール・管理リスナは一切影響を受けない。
    /// 閲覧系ルートは管理リスナ（loopback）に同居しているため、サーバ上の閲覧・証明書差し替え・
    /// HTTPS 無効化は引き続き可能）。
    /// レベルは警告——LAN からの閲覧は止まるが、機能停止ではなく opt-in 面の縮退であり、
    /// 復旧動線（loopback 管理面）が構造的に残るため <see cref="AdminHttpsCertificateUnavailableAtStartup"/>
    /// （1013）・<see cref="IngestionTlsCertificateUnavailableAtStartup"/>（1016）と同区分とする
    /// （ADR-0022 委任 1 の吟味——「機能停止を伴う事象はエラー」原則との照合の結論。
    /// 稼働中の継続的な可視化は 1018 同型の周期監視——実装は通知段——が担い、本 ID は起動時の
    /// 1 回に限る）。
    /// </summary>
    /// <remarks>
    /// 1033・1034 は ActiveNotificationEventIds 側（フォワーダ MSI 配置フォルダの
    /// ACL 検査。ADR-0020 決定 2）で使用済みのため、本イベントは 1035 を採る。
    /// </remarks>
    public static readonly EventId ViewerHttpsCertificateUnavailableAtStartup =
        new(1035, "ViewerHttpsCertificateUnavailableAtStartup");

    /// <summary>
    /// 「認証あり・平文・LAN」構成の起動時警告（ADR-0022 決定 5）。
    /// <c>Viewer:Authentication:Windows:AdminGroups</c> が非空（= 管理等価 Cookie が発行され得る）
    /// かつ閲覧 HTTPS が有効でなく、かつ公開範囲が LAN の組み合わせで 1 回だけ発する。
    /// <b>起動も機能も止めない</b>——連動強制（閲覧認証有効時の HTTPS 必須化）はオーナー裁定で
    /// 却下済みであり、本 ID は可視化のみを担う（「連動強制はしないが、無音にもしない」）。
    /// <c>ViewerGroups</c> のみの構成は対象外（焦点は管理等価 Cookie。警告のノイズ化を避ける）。
    /// レベルは警告。採番: 1036・1037 は ActiveNotificationEventIds 側（閲覧 HTTPS 証明書の
    /// 期限接近・稼働中使用不能）が使用済みのため 1038。
    /// </summary>
    public static readonly EventId ViewerAdminGroupsPlaintextExposure =
        new(1038, "ViewerAdminGroupsPlaintextExposure");
}
