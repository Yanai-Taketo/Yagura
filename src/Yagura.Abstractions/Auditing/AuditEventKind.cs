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
    /// <b>凍結（ADR-0011 決定 9）</b>: 本 ID は ADR-0011 の三層防御（バックオフ + IP レート制限 +
    /// グローバルトークンバケット）の採用以降、発火しなくなった——ハードロックアウト機構自体が
    /// supersede されたため。意味・レベルは変更しない（additive-only 規約の「凍結」扱い。
    /// security.md §4.3）。後継の事象は <see cref="AdminAuthBackoffCapReached"/>（3006）・
    /// <see cref="AdminAuthRateLimited"/>（3007）。
    /// </remarks>
    AdminAccountLockedOut,

    /// <summary>
    /// 管理操作: 管理 UI へのサインイン成功（Windows 統合認証・アプリ独自認証の両方。
    /// ADR-0010 決定 6「誰が」欄の実効化の起点。イベント ID 2008）。
    /// <c>AuthenticationScheme</c>/<c>AuthenticatedPrincipal</c> を必ず伴う。
    /// </summary>
    AdminLoginSucceeded,

    /// <summary>
    /// 拒否・セキュリティ事象: 認証には成功したが管理者権限がないため管理 UI へのアクセスを拒否
    /// （Windows 統合認証で認証は成立したが <c>BUILTIN\Administrators</c> に所属していない等。
    /// イベント ID 3008。issue #237）。<c>AuthenticationScheme</c>/<c>AuthenticatedPrincipal</c> を伴う。
    /// </summary>
    /// <remarks>
    /// <b><see cref="WindowsAuthenticationHandshakeFailed"/>（3003）とは別事象</b>: 3003 は
    /// プロトコルレベルの握手失敗（トークン不正・SPN 不一致等——認証が成立していない）を表す。
    /// 本種別は「認証は成立したが認可で拒否された」を表し、名（握手失敗）と実（認証成功）の乖離を避け、
    /// 運用者が Kind だけで両者を切り分けられるようにする（Detail 文字列一致に頼らせない。issue #237）。
    /// </remarks>
    AdminAuthorizationDenied,

    /// <summary>
    /// 管理操作: 管理リスナ HTTPS 証明書の秘密鍵読み取り権限をサービスアカウントへ付与
    /// （ADR-0010 Phase 2 決定 4。イベント ID 2009）。記録内容は証明書拇印・付与先アカウントのみ。
    /// </summary>
    AdminHttpsCertificatePrivateKeyAccessGranted,

    /// <summary>
    /// 管理操作: TLS 受信（RFC 5425。opt-in）証明書の秘密鍵読み取り権限をサービスアカウントへ付与
    /// （security.md §6。<see cref="AdminHttpsCertificatePrivateKeyAccessGranted"/> と同型の起動時
    /// 自動操作。イベント ID 2010。Issue #137）。記録内容は証明書拇印・付与先アカウントのみ。
    /// </summary>
    IngestionTlsCertificatePrivateKeyAccessGranted,

    /// <summary>
    /// 拒否・セキュリティ事象: アプリ独自認証のアカウント単位バックオフが cap（上限遅延）に到達
    /// （ADR-0011 決定 3・9。イベント ID 3006）。<see cref="AppAuthenticationLoginFailed"/>（3004）は
    /// 通常の失敗ログインとして引き続き記録し、本 ID は「バックオフが上限まで達した」ことを示す
    /// 追加のセキュリティ事象として記録する。記録内容は送信元 IP・アカウントキー（アカウント正規化
    /// ユーザー名 × loopback/remote の別）・現在の連続失敗回数 n・算出された待機時間。
    /// </summary>
    AdminAuthBackoffCapReached,

    /// <summary>
    /// 拒否・セキュリティ事象: IP レート制限またはグローバルトークンバケットによる拒否
    /// （ADR-0011 決定 2・4・5.1・9。イベント ID 3007）。<c>Detail</c> で拒否理由の別
    /// （IP レート制限/グローバルトークンバケット涸渇のいずれか。後者はプロセス全体の事象である旨も
    /// 含める）を区別する——利用者応答では区別しない（決定 3）。記録内容は送信元 IP・拒否理由の別。
    /// </summary>
    AdminAuthRateLimited,

    /// <summary>
    /// 管理操作: 管理リスナのリモートバインド（<c>Admin:RemoteBinding:Enabled</c>）の有効化・無効化
    /// （ADR-0012 決定 7。「機の公開」という最重要のセキュリティ状態遷移を認証設定変更（2006）に
    /// 畳み込まず独立 ID で記録する。イベント ID 2011）。
    /// </summary>
    AdminRemoteBindingConfigured,

    /// <summary>
    /// 管理操作: 管理リスナのリモート HTTPS 設定（<c>Admin:Https:Enabled</c>・証明書拇印・ポート）の
    /// 変更（ADR-0012 決定 7。イベント ID 2012）。記録内容は変更キーと新値（拇印は証明書の公開識別子
    /// であり秘密ではないため値を残す）。
    /// </summary>
    AdminHttpsCertificateConfigured,

    /// <summary>
    /// 管理操作: 認証セッションの緊急全失効（ADR-0013 決定 2。イベント ID 2013）。セッション世代番号を
    /// バンプして発行済みの全認証セッション Cookie を即時無効化する。記録内容は無効化後の世代番号
    /// （＝無効化した母集団の識別）・実行者（<c>AuthenticationScheme</c>/<c>AuthenticatedPrincipal</c>）。
    /// </summary>
    AdminSessionsInvalidated,

    /// <summary>
    /// 閲覧リスナ（8514）へのサインイン成功（ADR-0010 Phase 4 決定 7。イベント ID 2014）。閲覧認証
    /// opt-in 有効時、Windows 統合認証（AD グループ → 閲覧/管理役割）またはアプリ独自認証（管理役割）で
    /// 閲覧リスナ経由にセッションが確立したことを、管理リスナのサインイン成功（<see cref="AdminLoginSucceeded"/>
    /// = 2008）と区別して記録する。<c>Detail</c> に役割（<c>role=viewer</c>/<c>role=admin</c>）と方式を残す。
    /// <c>AuthenticationScheme</c>/<c>AuthenticatedPrincipal</c> を必ず伴う。
    /// </summary>
    ViewerLoginSucceeded,

    /// <summary>
    /// 拒否・セキュリティ事象: 閲覧リスナで Windows 統合認証は成功したが、設定された閲覧/管理いずれの
    /// AD グループにも所属していないためアクセスを拒否（ADR-0010 Phase 4 決定 7・SEC-9。イベント ID 3009）。
    /// 管理側の <see cref="AdminAuthorizationDenied"/>（3008。544/管理グループ非該当）と対をなす閲覧側事象。
    /// <c>AuthenticationScheme</c>/<c>AuthenticatedPrincipal</c> を伴う。
    /// </summary>
    ViewerAuthorizationDenied,

    /// <summary>
    /// 監査記録の保持期間削除の実行（security.md §4.2 SEC-2。保持期間 365 日を超過した監査記録
    /// ファイルの削除。イベント ID 2015。Issue #261）。<c>Detail</c> に削除ファイル数・保持日数・
    /// cutoff（UTC）・削除したファイル名を記録する——**証跡の削除自体を証跡に残す**（イベントログ
    /// 併記により、監査ファイル側の記録が消されてもイベントログに削除の事実が残る。ADR-0004
    /// 決定 7「消去が痕跡を残す」と同じ向き）。システムが定時実行する自動操作のため
    /// <c>RemoteAddress</c>/<c>AuthenticationScheme</c> は <see langword="null"/>。
    /// </summary>
    AuditRetentionApplied,

    /// <summary>
    /// 管理操作: 設定ファイル（手編集）のライブ再読み込みの実行（configuration.md §3。CF-4 層1。
    /// イベント ID 2016。Issue #262）。UI 経由・SCM カスタム制御コード経由（CF-5）の両方が
    /// 本種別に合流する。<c>Detail</c> に変更キー・適用キー・再起動待ちキーの要約を記録する
    /// （前後値は含めない——<see cref="ConfigurationSaved"/> と同じ「キー名 + 反映方式」の粒度。
    /// 秘密情報キーの値の混入を構造的に避ける）。設定変更の保存（2001 = ウィザード経由の
    /// ファイル書き込み）とは別事象——本種別は「実行中プロセスへの反映」を表す。
    /// </summary>
    ConfigurationReloaded,

    /// <summary>
    /// 管理操作: インストール記録（ファイアウォール規則一覧・オプトアウト選択——
    /// <c>firewall-rules.ini</c>）の初回起動時のイベントログ転記（configuration.md §4.3・
    /// security.md §4.1「インストーラ由来の記録の転記」。イベント ID 2017。Issue #265）。
    /// 「なぜこのサーバには規則がないのか」に証跡で答える。インストーラの実行記録の転記のため
    /// <c>RemoteAddress</c>/<c>AuthenticationScheme</c> は <see langword="null"/>。
    /// </summary>
    InstallationRecordTranscribed,

    /// <summary>
    /// 管理操作: 蓄積ログ移行（SQLite → SQL Server。database.md §6.2。DB-5。イベント ID 2018。
    /// Issue #266）の実行。<c>Detail</c> に結果（検証の合否・移行元件数・累計移行件数・
    /// 移行先範囲内件数）を記録する。
    /// </summary>
    LogMigrationExecuted,
}
