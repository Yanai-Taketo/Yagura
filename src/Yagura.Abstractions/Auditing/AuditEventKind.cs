namespace Yagura.Abstractions.Auditing;

/// <summary>
/// 監査記録の事象種別（security.md §4.1・§4.3）。
/// </summary>
/// <remarks>
/// <para>
/// M6-2（Issue #52）で <see cref="ViewerListenerAdminRequestRejected"/> を、
/// M8-4（Issue #71）で管理操作（2000 番台）と origin 拒否を追加した。
/// 他の値（認証失敗・失効猶予の記録等）は該当機能の実装時に追加する
/// （security.md §4.1 の対象一覧・§4.4「事象種別が変われば個別記録する」の前提となる区分）。
/// </para>
/// <para>
/// <b>配置（M8-4 で <c>Yagura.Storage.Auditing</c> から移設）</b>: 監査記録はログ本体の
/// 永続化（provider 抽象）とは独立した「ホスト管轄のローカルファイル + イベントログ併記」
/// であり（security.md §4.2）、Storage の管轄物ではない。モジュール横断契約の最下層
/// <c>Yagura.Abstractions</c>（architecture.md §1.1）へ移設した——M6-4 の申し送り
/// 「<c>IAuditRecorder</c> 等、現在 <c>Yagura.Storage</c> にある横断契約の移設も M8 で判断する」の決着。
/// </para>
/// </remarks>
public enum AuditEventKind
{
    /// <summary>
    /// 閲覧リスナに到達した管理系要求の拒否（security.md §1 L-3b。イベント ID 3001）。
    /// </summary>
    ViewerListenerAdminRequestRejected,

    /// <summary>
    /// 管理操作: 設定変更の適用（初期セットアップウィザードによる設定ファイル生成を含む。
    /// security.md §4.1「設定変更」。イベント ID 2001。M8-4）。
    /// </summary>
    ConfigurationSaved,

    /// <summary>
    /// 管理操作: 本番昇格の準備フェーズにおける SQL Server 接続検証の実施
    /// （database.md §6.1 準備フェーズ。管理者資格情報の使用の事実のみを記録し、
    /// 資格情報そのものは記録しない——configuration.md §5。イベント ID 2002。M8-4）。
    /// </summary>
    PromotionConnectionValidated,

    /// <summary>
    /// 管理操作: 本番昇格の切替実行（database.md §6.1。security.md §4.1「DB 切替・昇格」。
    /// イベント ID 2003。M8-4）。
    /// </summary>
    PromotionExecuted,

    /// <summary>
    /// 管理操作: circuit の個別切断（security.md §2.2「管理操作として監査対象」。
    /// イベント ID 2004。M8-4）。
    /// </summary>
    CircuitDisconnected,

    /// <summary>
    /// 同一サイト以外からの circuit 確立試行の拒否（origin 検証。security.md §2.1・§4.1
    /// 「origin 検証拒否」。イベント ID 3002。M8-4）。
    /// </summary>
    CircuitOriginRejected,

    /// <summary>
    /// 管理操作: フォワーダ配布キットの生成（ADR-0008 設計条件 6。イベント ID 2005）。
    /// 記録内容は生成日時・宛先（ホスト・ポート）・収集チャネル。秘密情報は含まない。
    /// </summary>
    ForwarderKitGenerated,

    /// <summary>
    /// 管理操作: 管理 UI 認証設定の変更（ADR-0010 決定 1・3。Windows 統合認証/アプリ独自認証の
    /// 有効化・Kerberos-only・loopback 認証 opt-in の切替。イベント ID 2006）。
    /// </summary>
    AdminAuthenticationConfigured,

    /// <summary>
    /// 管理操作: アプリ独自認証の管理者アカウントの作成・パスワード変更（ADR-0010 決定 3。
    /// パスワードそのものは記録しない。イベント ID 2007）。
    /// </summary>
    AdminAccountCreated,

    /// <summary>
    /// 拒否: Windows 統合認証（Negotiate）のハンドシェイク失敗・拒否（ADR-0010 決定 6。
    /// イベント ID 3003）。
    /// </summary>
    WindowsAuthenticationHandshakeFailed,

    /// <summary>
    /// 拒否: アプリ独自認証のログイン失敗（ADR-0010 決定 6。試行されたユーザー名を保持する。
    /// イベント ID 3004）。
    /// </summary>
    AppAuthenticationLoginFailed,

    /// <summary>
    /// 拒否・セキュリティ事象: アプリ独自認証アカウントのロックアウト発生（ADR-0010 決定 6。
    /// イベント ID 3005）。
    /// </summary>
    /// <remarks>
    /// <b>「解除」側の事象は Phase 1 では定義しない</b>（PR #217 レビュー指摘の決着）:
    /// Phase 1 のロックアウト解除は時間経過による自動失効のみであり、「解除」という個別の
    /// 事象が発生する時点が存在しない（明示的な解除操作も未実装）。到達不能な事象種別を
    /// 定義したままにせず、管理者による明示的なロックアウト解除操作を実装する際に
    /// 新しい ID とともに追加する（申し送り: security.md §2.4）。
    /// </remarks>
    AdminAccountLockedOut,

    /// <summary>
    /// 管理操作: 管理 UI へのサインイン成功（Windows 統合認証・アプリ独自認証の両方。
    /// ADR-0010 決定 6「誰が」欄の実効化の起点。イベント ID 2008）。
    /// <c>AuthenticationScheme</c>/<c>AuthenticatedPrincipal</c> を必ず伴う。
    /// </summary>
    AdminLoginSucceeded,
}
