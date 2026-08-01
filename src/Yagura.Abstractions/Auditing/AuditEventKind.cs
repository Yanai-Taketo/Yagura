namespace Yagura.Abstractions.Auditing;

/// <summary>
/// 監査記録の事象種別（security.md §4.1・§4.3）。
/// </summary>
/// <remarks>
/// 監査記録はログ本体の永続化（provider 抽象）とは独立した「ホスト管轄のローカルファイル +
/// イベントログ併記」であり（security.md §4.2）、モジュール横断契約の最下層
/// <c>Yagura.Abstractions</c> に置く（architecture.md §1.1）。値は additive-only
/// （既存値の意味・レベルは変更しない。security.md §4.3）。
/// </remarks>
public enum AuditEventKind
{
    /// <summary>
    /// 閲覧リスナに到達した管理系要求の拒否（security.md §1 L-3b。イベント ID 3001）。
    /// </summary>
    ViewerListenerAdminRequestRejected,

    /// <summary>
    /// 管理操作: 設定変更の適用（初期セットアップウィザードによる設定ファイル生成を含む。
    /// security.md §4.1「設定変更」。イベント ID 2001）。
    /// </summary>
    ConfigurationSaved,

    /// <summary>
    /// 管理操作: 本番昇格の準備フェーズにおける SQL Server 接続検証の実施
    /// （database.md §6.1 準備フェーズ。資格情報の使用の事実のみを記録し、
    /// 資格情報そのものは記録しない——configuration.md §5。イベント ID 2002）。
    /// </summary>
    PromotionConnectionValidated,

    /// <summary>
    /// 管理操作: 本番昇格の切替実行（database.md §6.1。security.md §4.1「DB 切替・昇格」。
    /// イベント ID 2003）。
    /// </summary>
    PromotionExecuted,

    /// <summary>
    /// 管理操作: circuit の個別切断（security.md §2.2「管理操作として監査対象」。
    /// イベント ID 2004）。
    /// </summary>
    CircuitDisconnected,

    /// <summary>
    /// 同一サイト以外からの circuit 確立試行の拒否（origin 検証。security.md §2.1・§4.1
    /// 「origin 検証拒否」。イベント ID 3002）。
    /// </summary>
    CircuitOriginRejected,

    /// <summary>
    /// 管理操作: フォワーダ配布キットの生成（ADR-0008 設計条件 6。イベント ID 2005）。
    /// 記録内容は生成日時・宛先（ホスト・ポート）・収集チャネル。秘密情報は含まない。
    /// </summary>
    ForwarderKitGenerated,

    /// <summary>
    /// 管理操作: 管理 UI 認証設定の変更（Windows 統合認証/アプリ独自認証の有効化・Kerberos-only・
    /// loopback 認証 opt-in の切替。ADR-0010 決定 3。イベント ID 2006）。
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
    /// イベント ID 3005）。凍結——ADR-0011 の三層防御採用後は発火しない。後継は
    /// <see cref="AdminAuthBackoffCapReached"/>（3006）・<see cref="AdminAuthRateLimited"/>（3007）。
    /// </summary>
    AdminAccountLockedOut,

    /// <summary>
    /// 管理操作: 管理 UI へのサインイン成功（Windows 統合認証・アプリ独自認証の両方。
    /// ADR-0010 決定 6「誰が」欄の実効化の起点。イベント ID 2008）。
    /// <c>AuthenticationScheme</c>/<c>AuthenticatedPrincipal</c> を必ず伴う。
    /// </summary>
    AdminLoginSucceeded,

    /// <summary>
    /// 拒否・セキュリティ事象: 認証には成功したが管理者権限がないため管理 UI へのアクセスを拒否
    /// （<c>BUILTIN\Administrators</c> 非所属等。イベント ID 3008）。<see cref="WindowsAuthenticationHandshakeFailed"/>
    /// （3003。プロトコルレベルの握手失敗）とは異なり、認証は成立したが認可で拒否された事象。
    /// <c>AuthenticationScheme</c>/<c>AuthenticatedPrincipal</c> を必ず伴う。
    /// </summary>
    AdminAuthorizationDenied,

    /// <summary>
    /// 管理操作: 管理リスナ HTTPS 証明書の秘密鍵読み取り権限をサービスアカウントへ付与
    /// （ADR-0010 Phase 2 決定 4。イベント ID 2009）。記録内容は証明書拇印・付与先アカウントのみ。
    /// </summary>
    AdminHttpsCertificatePrivateKeyAccessGranted,

    /// <summary>
    /// 管理操作: TLS 受信（RFC 5425。opt-in）証明書の秘密鍵読み取り権限をサービスアカウントへ付与
    /// （security.md §6。<see cref="AdminHttpsCertificatePrivateKeyAccessGranted"/> と同型の起動時
    /// 自動操作。イベント ID 2010）。記録内容は証明書拇印・付与先アカウントのみ。
    /// </summary>
    IngestionTlsCertificatePrivateKeyAccessGranted,

    /// <summary>
    /// 管理操作: 閲覧 UI HTTPS（ADR-0022。opt-in）証明書の秘密鍵読み取り権限をサービスアカウントへ付与
    /// （configuration.md §6。<see cref="AdminHttpsCertificatePrivateKeyAccessGranted"/> と同型の
    /// 起動時自動操作。イベント ID 2028）。記録内容は証明書拇印・付与先アカウントのみ。
    /// </summary>
    ViewerHttpsCertificatePrivateKeyAccessGranted,

    /// <summary>
    /// 拒否・セキュリティ事象: アプリ独自認証のアカウント単位バックオフが cap（上限遅延）に到達
    /// （ADR-0011 決定 9。イベント ID 3006）。<see cref="AppAuthenticationLoginFailed"/>（3004。通常の
    /// 失敗ログイン）とは別に、上限到達を示す追加のセキュリティ事象として記録する。記録内容は
    /// 送信元 IP・アカウントキー・連続失敗回数・待機時間。
    /// </summary>
    AdminAuthBackoffCapReached,

    /// <summary>
    /// 拒否・セキュリティ事象: IP レート制限またはグローバルトークンバケットによる拒否
    /// （ADR-0011 決定 9。イベント ID 3007）。<c>Detail</c> で拒否理由の別を区別する
    /// （利用者応答では区別しない）。記録内容は送信元 IP・拒否理由の別。
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
    /// （＝無効化した母集団の識別）・実行者。
    /// </summary>
    AdminSessionsInvalidated,

    /// <summary>
    /// 閲覧リスナ（8514）へのサインイン成功（ADR-0010 Phase 4 決定 7。イベント ID 2014）。
    /// Windows 統合認証（AD グループ → 閲覧/管理役割）またはアプリ独自認証で確立したセッションを、
    /// 管理リスナのサインイン成功（<see cref="AdminLoginSucceeded"/> = 2008）と区別して記録する。
    /// <c>Detail</c> に役割と方式を残す。
    /// </summary>
    ViewerLoginSucceeded,

    /// <summary>
    /// 拒否・セキュリティ事象: 閲覧リスナで Windows 統合認証は成功したが、設定された閲覧/管理いずれの
    /// AD グループにも所属していないためアクセスを拒否（ADR-0010 Phase 4 決定 7・SEC-9。イベント ID 3009）。
    /// 管理側の <see cref="AdminAuthorizationDenied"/>（3008。管理グループ非該当）と対をなす閲覧側事象。
    /// </summary>
    ViewerAuthorizationDenied,

    /// <summary>
    /// 監査記録の保持期間削除の実行（security.md §4.2 SEC-2。保持 365 日超過分の削除。
    /// イベント ID 2015）。証跡の削除自体を証跡に残す（イベントログ併記）。システムの定時自動操作の
    /// ため <c>RemoteAddress</c>/<c>AuthenticationScheme</c> は <see langword="null"/>。
    /// </summary>
    AuditRetentionApplied,

    /// <summary>
    /// 管理操作: 設定ファイル（手編集）のライブ再読み込みの実行（configuration.md §3。イベント ID 2016）。
    /// UI 経由・SCM カスタム制御コード経由の両方が本種別に合流する。
    /// <see cref="ConfigurationSaved"/>（設定ファイル書き込み）とは別——実行中プロセスへの反映を表す。
    /// </summary>
    ConfigurationReloaded,

    /// <summary>
    /// 管理操作: インストール記録（ファイアウォール規則一覧・オプトアウト選択）の初回起動時の
    /// イベントログ転記（configuration.md §4.3。イベント ID 2017）。インストーラの実行記録の転記の
    /// ため <c>RemoteAddress</c>/<c>AuthenticationScheme</c> は <see langword="null"/>。
    /// </summary>
    InstallationRecordTranscribed,

    /// <summary>
    /// 管理操作: 蓄積ログ移行（SQLite → SQL Server。database.md §6.2。イベント ID 2018）の実行。
    /// <c>Detail</c> に検証の合否・移行件数を記録する。
    /// </summary>
    LogMigrationExecuted,

    /// <summary>
    /// セキュリティ事象: 認証失効後の閲覧 circuit の継続を猶予として許容した（SEC-6。
    /// security.md §2.3。イベント ID 3010）。「失効から遮断までに誰が何を見得たか」に監査で答える起点。
    /// </summary>
    CircuitRevocationGraceGranted,

    /// <summary>
    /// セキュリティ事象: 猶予中だった circuit の終了（SEC-6。security.md §2.3。イベント ID 3011）。
    /// 3010 と対で「見得た期間」を閉じる。
    /// </summary>
    CircuitRevocationGraceEnded,

    /// <summary>
    /// セキュリティ事象: 拒否試行の集約記録（SEC-4。security.md §4.4。イベント ID 3012）。
    /// 同一送信元・同一種別の拒否が短時間に反復した場合、個別記録から集約記録へ切り替えた結果の
    /// サマリ。試行された利用者名の集合は残す——件数に畳まない（§4.4）。
    /// </summary>
    RejectionAggregated,

    /// <summary>
    /// セキュリティ事象: 監査チャネルの復旧と、障害中に保持していた事象の書き戻し完了（SEC-10。
    /// security.md §4.2。イベント ID 3013）。「証跡が欠けた可能性のある期間」自体を証跡に残す。
    /// レベルは警告（欠落可能性を既定の監視で拾えるようにする）。
    /// </summary>
    AuditChannelRecovered,

    /// <summary>
    /// 管理操作: 前回稼働時から設定ファイルが変更された状態で起動した（起動時の設定差分照合。
    /// security.md §4.1。イベント ID 2019）。前回適用スナップショットとの差分があるときに 1 件記録する。
    /// 「いつ・誰が」変更したかは特定できない——統制は OS の ACL・SACL に委ねる。起動時の自動照合の
    /// ため <c>RemoteAddress</c>/<c>AuthenticationScheme</c> は <see langword="null"/>。
    /// </summary>
    StartupConfigurationChangeDetected,

    /// <summary>
    /// 管理操作: TLS 受信の証明書設定の変更（ADR-0019 決定 5。イベント ID 2020）。
    /// <see cref="AdminHttpsCertificateConfigured"/>（= 2012）と対になる TLS 受信版で、記録内容も同型
    /// （拇印は公開識別子のため値を残す）。<c>Port</c> も対象——到達面の変更は監査価値が高いため。
    /// </summary>
    IngestionTlsCertificateConfigured,

    /// <summary>
    /// 管理操作: 閲覧 UI の HTTPS 設定の変更（イベント ID 2029。ADR-0022 決定 10）。
    /// 2012（管理リモート HTTPS）・2020（TLS 受信）の閲覧版であり、記録内容も同型。
    /// </summary>
    ViewerHttpsConfigured,

    /// <summary>
    /// メール通知設定の変更（イベント ID 2021。ADR-0017 決定 4）。<c>Smtp:Password</c> は「変更した」
    /// 事実のみで値は残さない。宛先・接続先の値は残す——「通知がどこへ向かうか」を事後に追えるため。
    /// </summary>
    EmailNotificationConfigured,

    /// <summary>
    /// メール通知のテスト送信（イベント ID 2022。ADR-0017 決定 8）。状態を変えない操作だが監査対象
    /// ——任意のホスト・ポートへの接続試行は内部ネットワークの到達性探査に転用し得るため。
    /// 資格情報の値そのものは記録しない。
    /// </summary>
    EmailNotificationTestSent,

    /// <summary>
    /// 送信元の途絶検知のウォッチリスト変更（イベント ID 2023。ADR-0018 決定 5）。追加・削除・変更
    /// されたエントリ（アドレスと表示名）を必ず記録する——ウォッチリストは検知範囲そのものの定義であり、
    /// 事後にエントリ操作を再構成できる粒度が要る。
    /// </summary>
    SourceSilenceWatchlistConfigured,

    /// <summary>
    /// 管理操作: サービス実行アカウントの構成の初回起動時転記（ADR-0015 決定 8。イベント ID 2024）。
    /// インストーラが書く構成記録（<c>service-account.ini</c>）を初回起動時に 1 回だけイベントログへ
    /// 転記する（一回性はマーカーファイルで判定）。起動時の自動転記のため
    /// <c>RemoteAddress</c>/<c>AuthenticationScheme</c> は <see langword="null"/>。
    /// </summary>
    ServiceAccountTranscribed,

    /// <summary>
    /// 管理操作: サービス実行アカウントが前回起動時から変化した状態で起動した（ADR-0015 決定 8。
    /// イベント ID 2025）。実効実行アカウントを前回記録と照合し、変化があれば旧・新のアカウント名を
    /// 記録する——製品外の <c>sc config</c> による切替も次回起動で証跡化される。起動時の自動照合の
    /// ため <c>RemoteAddress</c>/<c>AuthenticationScheme</c> は <see langword="null"/>。
    /// </summary>
    ServiceAccountChangeDetected,

    /// <summary>
    /// 管理操作: フォワーダ MSI を管理画面から配置（アップロード）した（ADR-0020 決定 4。
    /// イベント ID 2026）。ステージング → 検証 → 確認 → アトミックリネームの全段を通過した確定時に
    /// 1 件記録する。認証必須構成でのみ実行可能なため
    /// <c>AuthenticationScheme</c>/<c>AuthenticatedPrincipal</c> には必ず値が入る。
    /// </summary>
    ForwarderMsiPlaced,

    /// <summary>
    /// 管理操作: 配置済みのフォワーダ MSI を管理画面から削除した（ADR-0020 決定 4。イベント ID 2027）。
    /// 削除は配置と同じ重みの操作として扱う。<c>AuthenticationScheme</c>/<c>AuthenticatedPrincipal</c>
    /// は 2026 と同じく必ず値が入る。
    /// </summary>
    ForwarderMsiDeleted,

    /// <summary>
    /// 拒否・セキュリティ事象: フォワーダ MSI のアップロード・削除の失敗/拒否
    /// （ADR-0020 決定 4。イベント ID 3014。拒否された試行も監査対象）。検証失敗・二段階確認の拒否・
    /// 書き込み失敗・排他拒否・確認不整合を <c>Detail</c> の理由種別で区別する。§4.4 の集約対象。
    /// </summary>
    ForwarderMsiUploadRejected,

    /// <summary>
    /// 保存先（アカウント台帳）が到達不能なため、アプリ独自認証のログイン要求を「一時的に
    /// 利用できない」として拒否した（ADR-0023 決定 1。イベント ID 3015）。3004（ログイン失敗）へは
    /// 相乗りさせない——資格情報の検証に到達していない事象のため。
    /// </summary>
    AdminAccountStoreUnavailableRejected,
}
