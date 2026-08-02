namespace Yagura.Web.Components.Common;

public static partial class UiText
{
    // ---- 蓄積ログの移行（database.md §6.2。DB-5） ----

    /// <summary>移行セクションのタイトル。</summary>
    public const string MigrationSectionTitle = "蓄積ログの移行（旧 SQLite → SQL Server）";

    /// <summary>移行の説明。</summary>
    public const string MigrationDescription =
        "本番昇格前に SQLite へ保存されたログを、現在の SQL Server へ移送します。移行中も受信は継続します。" +
        "移行しない場合、旧ログは閲覧できないまま、旧データベースの処分で失われます。";

    /// <summary>移行の実行ボタン。</summary>
    public const string MigrationExecute = "移行を実行";

    /// <summary>移行の進捗書式（{0} = 移行済み件数・{1} = 総件数）。</summary>
    public const string MigrationProgressFormat = "移行中: {0:N0} / {1:N0} 件";

    /// <summary>移行の完了通知。</summary>
    public const string MigrationCompletedNotification = "蓄積ログの移行が完了しました";

    /// <summary>移行の検証不合格通知。</summary>
    public const string MigrationFailedNotification = "移行の完全性検証に不合格です（再実行で追補できます）";

    /// <summary>移行完了済みの表示。</summary>
    public const string MigrationAlreadyCompleted =
        "蓄積ログの移行は完了しています。旧データベースファイルの処分は処分手順に従ってください。";

    // ---- 設定の再読み込み（configuration.md §3。CF-4 層1） ----

    /// <summary>設定再読み込み画面のタイトル。</summary>
    public const string AdminReloadTitle = "設定の再読み込み";

    /// <summary>設定再読み込み画面の説明。</summary>
    public const string AdminReloadDescription =
        "手編集された設定ファイル（yagura.json）を読み直し、変更のうち即時反映できる項目を適用します。" +
        "反映にサービス再起動が必要な項目は、未反映のまま残る項目として下に表示されます。";

    /// <summary>再読み込みの実行ボタン。</summary>
    public const string AdminReloadExecute = "再読み込みを実行";

    /// <summary>再読み込みが検証失敗で拒否されたときの前置き。</summary>
    public const string AdminReloadRejected = "設定に不正があるため再読み込みを中止しました（実行中の構成は変更されていません）: ";

    /// <summary>変更なしの結果表示。</summary>
    public const string AdminReloadNoChanges = "設定ファイルに変更はありませんでした。";

    /// <summary>適用件数の書式（{0} = 適用キー数）。</summary>
    public const string AdminReloadAppliedFormat = "再読み込みを実行しました（即時反映: {0} 件）。";

    /// <summary>適用キー一覧のラベル。</summary>
    public const string AdminReloadAppliedKeys = "反映した項目";

    /// <summary>再起動待ちキーの前置き。</summary>
    public const string AdminReloadPendingRestart =
        "次の項目は反映にサービス再起動が必要なため、未反映のまま残っています:";

    /// <summary>検証警告の前置き。</summary>
    public const string AdminReloadWarnings = "設定値の警告（不正な値は既定値・安全側で継続しています）:";

    /// <summary>未知キーの前置き。</summary>
    public const string AdminReloadUnknownKeys = "認識されない設定キー（誤記の可能性）: ";

    /// <summary>
    /// 型の読み替え一覧の前置き（情報レベル——受理は正常系であり警告にしない。
    /// configuration.md §1）。
    /// </summary>
    public const string AdminReloadTypeCoercions = "文字列以外の JSON 型で書かれ、そのまま受理した設定キー（動作への影響はありません）:";

    /// <summary>
    /// 保存後の自動再読み込み（再起動待ちカードへの計上）が拒否されたときの通知。
    /// {0} に拒否理由が入る。保存自体は成立している。
    /// </summary>
    public const string AdminSaveReloadRejectedFormat =
        "保存は完了しましたが、再起動待ちへの計上（設定の再読み込み）が拒否されました: {0}。設定の再読み込み画面から手動で実行してください";

    // ---- 管理面入口のメール通知チャネル健全性カード（ADR-0017 決定 5） ----

    /// <summary>カードの見出し。</summary>
    public const string AdminEmailHealthCardTitle = "メール通知チャネル";

    /// <summary>
    /// カードの説明（日常動線での常設表示——チャネルの静かな死〔パスワード失効・リレー廃止等〕に
    /// 気づける経路。at-most-once + 破棄の設計はこの常設表示があって正当化される——決定 5）。
    /// </summary>
    public const string AdminEmailHealthCardDescription =
        "メール通知は届かないことがあります（正本はイベントログ）。送信の健全性はここで確認できます。";

    /// <summary>詳細（設定画面）への導線。</summary>
    public const string AdminEmailHealthCardDetailLink = "詳細と設定（テスト送信・抑制の内訳）";

    /// <summary>再読み込み完了の通知。</summary>
    public const string AdminReloadCompletedNotification = "設定を再読み込みしました";

    /// <summary>再読み込み拒否の通知。</summary>
    public const string AdminReloadRejectedNotification = "設定の再読み込みを中止しました（設定値を確認してください）";

    // ---- ウィザード保存後の自動反映 ----

    /// <summary>保存後すぐ反映するチェックボックスのラベル（既定オン）。</summary>
    public const string WizardAutoApplyLabel = "保存後すぐ反映する";

    /// <summary>保存後すぐ反映するチェックボックスの補足。</summary>
    public const string WizardAutoApplyHelp =
        "オンにすると、保存の直後に即時反映できる項目をライブ反映します（設定の再読み込みと同じ経路）。" +
        "オフにすると保存のみを行い、反映は再読み込み操作または再起動で行います（複数の変更をまとめてから一括反映する運用向け）。";

    /// <summary>自動反映の実行結果（{0} = 即時反映のキー数）。</summary>
    public const string WizardAutoApplyAppliedFormat = "保存後の自動反映を実行しました（即時反映: {0} 件）。";

    /// <summary>自動反映で反映が必要な変更がなかったときの表示。</summary>
    public const string WizardAutoApplyNoChanges = "保存後の自動反映: 反映が必要な変更はありませんでした。";

    /// <summary>自動反映が検証失敗で実行できなかったときの前置き（保存自体は完了している）。</summary>
    public const string WizardAutoApplyRejected =
        "保存は完了しましたが、自動反映は実行できませんでした（設定の検証失敗——実行中の構成は変更されていません）: ";

    // ---- 再起動待ちキーの常設表示（管理面のみ） ----

    /// <summary>再起動待ちカードのタイトル。</summary>
    public const string AdminPendingRestartCardTitle = "再起動待ちの設定変更";

    /// <summary>再起動待ちカードの説明。</summary>
    public const string AdminPendingRestartCardDescription =
        "設定の再読み込みで変更が検出されましたが、反映にサービス再起動が必要なため未反映のまま残っている項目です。" +
        "サービスを再起動すると反映され、この表示は消えます。";

    /// <summary>再起動待ちキーの検出時刻の前置きラベル（後ろに時刻表示が続く）。</summary>
    public const string AdminPendingRestartDetectedAtLabel = "検出した再読み込み: ";

    // ---- 管理画面共通（ui.md §4「設定（ウィザード群）」。M8-4） ----

    /// <summary>
    /// 管理画面の circuit 層ガードの拒否表示（閲覧側からの到達。管理系パスの存在自体を
    /// 説明しない——ListenerPortGuardMiddleware の 404 と同じ判断）。
    /// </summary>
    public const string AdminScreenNotFound = "ページが見つかりません。";

    /// <summary>管理画面の circuit 層ガードで帰属を確認できない間の表示（fail-closed の中間状態）。</summary>
    public const string AdminScreenAccessChecking = "接続の帰属を確認しています…";

    /// <summary>設定トップ（/admin）の画面見出し（ui.md §4 の画面構成「設定（ウィザード群）」）。</summary>
    public const string AdminHomeTitle = "設定";

    /// <summary>初期セットアップウィザードへの導線・見出し。</summary>
    public const string AdminSetupWizardTitle = "初期セットアップ";

    /// <summary>本番昇格ウィザードへの導線・見出し（用語対応表: 本番昇格 → 保存先を SQL Server に切り替える）。</summary>
    public const string AdminPromotionWizardTitle = "保存先を SQL Server に切り替える";

    /// <summary>circuit 管理画面への導線・見出し。</summary>
    public const string AdminCircuitsTitle = "画面とサーバの接続の管理";

    // ---- 管理 UI 認証（ADR-0010 Phase 1） ----

    /// <summary>認証設定画面への導線・見出し。</summary>
    public const string AdminAuthSetupTitle = "管理 UI の認証";

    /// <summary>ログイン画面の見出し。</summary>
    public const string AdminLoginTitle = "サインイン";

    /// <summary>Windows 統合認証でのサインインボタン文言。</summary>
    public const string AdminLoginWindowsButton = "Windows でサインイン";

    /// <summary>アプリ独自認証のユーザー名入力欄ラベル。</summary>
    public const string AdminLoginUsernameLabel = "ユーザー名";

    /// <summary>アプリ独自認証のパスワード入力欄ラベル。</summary>
    public const string AdminLoginPasswordLabel = "パスワード";

    /// <summary>アプリ独自認証のサインインボタン文言。</summary>
    public const string AdminLoginAppButton = "サインイン";

    /// <summary>
    /// ログイン失敗時の汎用エラー文言（ユーザー列挙耐性のため、資格情報誤り・アカウント不在・
    /// バックオフ待機中のいずれも同一文言とする——security.md §4.3）。
    /// </summary>
    public const string AdminLoginError = "ユーザー名またはパスワードが正しくないか、現在サインインできません。";

    /// <summary>
    /// 保存先到達不能による一時的な利用不能（ADR-0023 決定 1）。**資格情報の誤りと混同させない**
    /// ——パスワードを忘れたと思い込ませてリセットを試みさせないため。保存先の名前・失敗の詳細は
    /// 出さない（未認証で観測できる情報を増やさない）。
    /// </summary>
    /// <summary>保存先到達不能による縮退の常設バナー見出し（ADR-0023 決定 1）。</summary>
    public const string AdminHomeStorageUnavailableTitle = "管理者アカウントの保存先に接続できていません（縮退運転中）";

    /// <summary>
    /// 同バナーの説明。**何が止まっていて何が動いているか**を分けて書く——縮退は「壊れている」
    /// でも「正常」でもない中間状態であり、どちらかに丸めると運用判断を誤らせる。範囲は
    /// <c>StorageAvailabilityState</c> が実際に表すもの（アプリ独自認証の可用性）に限る——
    /// ログ保存側の縮退はスプールの通知が別に担っており、ここで一緒くたに書くと不正確になる。
    /// </summary>
    public const string AdminHomeStorageUnavailableDescription =
        "管理者アカウントの台帳を初期化できないため、ID/パスワードでのサインインが一時的に"
        + "利用できません。ログの受信は継続しています。接続が復旧すると自動的に元に戻ります"
        + "（運用者の操作は不要です）。失敗の詳細は Windows イベントログを確認してください。";

    public const string AdminLoginStoreUnavailable =
        "ID/パスワードでのサインインは、現在一時的に利用できません（保存先の準備ができていません）。"
        + "パスワードの問題ではないため、変更や再設定は不要です。しばらく待って再度お試しください"
        + "——保存先が復旧すると自動的に利用できるようになります。"
        + "復旧しない場合は、サーバの管理者に Windows イベントログの確認を依頼してください。";

    /// <summary>
    /// 待機表示の統一文言（ADR-0011）。<c>{0}</c> に秒数を埋め込む。
    /// <b>用途は IP レート制限/グローバルトークンバケット拒否の 429 応答（アクセス集中）に限る</b>——
    /// これらは送信元 IP 単位・プロセス全体の状態で判定し、ユーザー名の実在有無に依存しないため
    /// カウントダウンを出しても列挙シグナルにならない。**アカウント単位バックオフには
    /// 使わない**——バックオフ待機を UI に出すと実在アカウントの存在を暴く（非開示要件。
    /// バックオフの効果はサーバ側の応答遅延としてのみ現れ、応答は誤パスワードと同一に統一する）。
    /// </summary>
    public const string AdminLoginWait = "しばらくお待ちください。あと {0} 秒で再試行できます。";

    /// <summary>管理画面が認証を要求しているが未認証のときの表示（AdminScreenLayout のリダイレクト経路の直前表示）。</summary>
    public const string AdminScreenRequiresAuthentication = "サインインが必要です…";

    // ---- 閲覧 UI 認証（ADR-0010 Phase 4 決定 7） ----

    /// <summary>閲覧ログイン画面の見出し。</summary>
    public const string ViewerLoginTitle = "閲覧 UI へのサインイン";

    /// <summary>
    /// 閲覧ログイン画面の説明（認証 opt-in 有効時のみ到達）。方式に依らず中立の文言にする——アプリ独自認証併用時
    /// （ID/パスワード欄も表示される）に「Windows でのサインインが必要」と読める固定文言だと誤解を招くため。
    /// 具体的な方式は各サインインボタン/フォームのラベルが示す。
    /// </summary>
    public const string ViewerLoginIntro = "このログを閲覧するにはサインインが必要です。";

    /// <summary>閲覧の Windows 統合認証サインインボタン文言。</summary>
    public const string ViewerLoginWindowsButton = "Windows でサインイン";

    /// <summary>閲覧のアプリ独自認証サインインボタン文言。</summary>
    public const string ViewerLoginAppButton = "サインイン";

    /// <summary>閲覧ログイン失敗時の汎用エラー文言（列挙耐性——管理ログインと同じ非開示方針）。</summary>
    public const string ViewerLoginError = "ユーザー名またはパスワードが正しくないか、閲覧を許可されたグループに所属していません。";

    /// <summary>閲覧画面が認証を要求しているが未認証のときの表示（MainLayout のリダイレクト経路の直前表示）。</summary>
    public const string ViewerScreenRequiresAuthentication = "サインインが必要です…";

    // ---- 管理 UI のリモートアクセス（HTTPS）設定（ADR-0012。/admin/remote-access） ----

    /// <summary>リモートアクセス設定画面への導線・見出し（ADR-0012 決定 1——認証設定とは分離した画面）。</summary>
    public const string AdminRemoteAccessTitle = "管理 UI のリモートアクセス（HTTPS）";

    /// <summary>画面の説明文（何をする画面か。対象キーはすべて反映 = サービス再起動——ADR-0012 決定 6）。</summary>
    public const string AdminRemoteAccessIntro =
        "管理画面をリモートの端末へ HTTPS で公開するための設定です。証明書はこのサーバの証明書ストアから選択でき、" +
        "拇印の手入力は不要です。";

    /// <summary>リモートバインド（Admin:RemoteBinding:Enabled）のスイッチ文言。</summary>
    public const string AdminRemoteAccessRemoteBindingLabel = "リモートの端末から管理画面へのアクセスを許可する";

    /// <summary>
    /// リモートバインドの前提条件の説明（fail-closed 不変条件を保存前に
    /// 利用者の言葉で示す——ADR-0012 決定 4。認証設定への相互リンクとセットで表示する）。
    /// </summary>
    public const string AdminRemoteAccessRemoteBindingNote =
        "有効化には、管理 UI の認証（Windows 統合認証またはアプリ独自認証）・HTTPS の有効化・証明書の選択が" +
        "すべて必要です。欠けたままでは保存できません（認証や通信保護を欠いたリモート公開を防ぐためです）。";

    /// <summary>認証設定画面への相互リンクの文言（ADR-0012 決定 1）。</summary>
    public const string AdminRemoteAccessAuthLinkText = "認証設定へ";

    /// <summary>認証設定画面側に置く、本画面への相互リンクの文言（ADR-0012 決定 1）。</summary>
    public const string AdminAuthSetupRemoteAccessLinkText = "リモートアクセス（HTTPS）設定へ";

    /// <summary>HTTPS 有効化（Admin:Https:Enabled）のスイッチ文言。</summary>
    public const string AdminRemoteAccessHttpsLabel = "リモート管理の HTTPS 待ち受けを有効にする";

    /// <summary>HTTPS ポート（Admin:Https:Port）の入力ラベル。</summary>
    public const string AdminRemoteAccessHttpsPortLabel = "HTTPS ポート";

    /// <summary>HTTPS ポートの補足（未設定時の既定値を明示する）。</summary>
    public const string AdminRemoteAccessHttpsPortHelp = "未入力の場合は既定の 8516 を使います。";

    /// <summary>現在の設定（保存済みの永続値）カードの見出し。</summary>
    public const string AdminRemoteAccessStatusTitle = "現在の設定（保存済みの値）";

    /// <summary>現在の設定: リモートアクセスの行ラベル。</summary>
    public const string AdminRemoteAccessStatusRemoteBinding = "リモートの端末からの管理アクセス";

    /// <summary>現在の設定: HTTPS の行ラベル。</summary>
    public const string AdminRemoteAccessStatusHttps = "HTTPS 待ち受け";

    /// <summary>現在の設定: 証明書拇印の行ラベル。</summary>
    public const string AdminRemoteAccessStatusThumbprint = "証明書の拇印";

    /// <summary>現在の設定: HTTPS ポートの行ラベル。</summary>
    public const string AdminRemoteAccessStatusPort = "HTTPS ポート";

    /// <summary>現在の設定: 有効。</summary>
    public const string AdminRemoteAccessStatusEnabled = "有効";

    /// <summary>現在の設定: 無効。</summary>
    public const string AdminRemoteAccessStatusDisabled = "無効";

    /// <summary>現在の設定: 未設定の値の表示。</summary>
    public const string AdminRemoteAccessStatusNotSet = "（未設定）";

    /// <summary>現在の設定: ポート未設定（既定値使用）の表示。</summary>
    public const string AdminRemoteAccessStatusDefaultPort = "（未設定——既定の 8516 を使用）";

    /// <summary>証明書選択カードの見出し（ADR-0012 決定 2 の本体）。</summary>
    public const string AdminRemoteAccessCertificatesTitle = "証明書の選択";

    /// <summary>
    /// 証明書一覧の説明（列挙範囲の最小化——serverAuth EKU + 秘密鍵あり——を利用者の言葉で明示する。
    /// ADR-0012 決定 2・受け入れ基準「拇印手貼りの撤廃」）。
    /// </summary>
    public const string AdminRemoteAccessCertificatesIntro =
        @"このサーバの証明書ストア（ローカル コンピューター\個人）のうち、サーバー認証（serverAuth）の用途と" +
        "秘密鍵を備えた証明書のみを表示しています。選択すると拇印が自動で設定されます（手入力は不要です）。";

    /// <summary>証明書一覧の再取得ボタン（付与・取り込みの後に、その場で結果を確認できる導線）。</summary>
    public const string AdminRemoteAccessRefreshButton = "再確認";

    /// <summary>証明書行: 発行者のラベル。</summary>
    public const string AdminRemoteAccessCertIssuerLabel = "発行者";

    /// <summary>証明書行: 有効期間のラベル。</summary>
    public const string AdminRemoteAccessCertValidityLabel = "有効期間";

    /// <summary>証明書行: 拇印のラベル。</summary>
    public const string AdminRemoteAccessCertThumbprintLabel = "拇印";

    /// <summary>証明書の状態バッジ: 使用可能。</summary>
    public const string AdminRemoteAccessCertOkBadge = "使用可能";

    /// <summary>証明書の状態バッジ: 有効期間外（期限切れ／未来証明書）。</summary>
    public const string AdminRemoteAccessCertExpiredBadge = "有効期間外";

    /// <summary>証明書の状態バッジ: サービスアカウントが秘密鍵を読めない。</summary>
    public const string AdminRemoteAccessCertKeyUnreadableBadge = "秘密鍵の読取権限なし";

    /// <summary>
    /// 有効期間外の証明書の警告（選択自体は可能だが保存時に拒否される旨——保存前検証
    /// （<c>AdminRemoteAccessAdminService</c>）と同じ理由で説明する。ADR-0012 決定 4 = D-6）。
    /// </summary>
    public const string AdminRemoteAccessCertExpiredWarning =
        "この証明書は有効期間外のため、保存時に拒否されます。起動時の証明書解決は有効期間外の証明書を" +
        "受け付けず、リモート HTTPS の待ち受けが開かれない縮小継続になるためです。";

    /// <summary>
    /// 秘密鍵をサービスアカウントが読めない証明書の警告（ADR-0012 決定 3 = (b)。保存は可能だが
    /// 付与しないまま再起動すると縮小継続になる旨を明示する）。
    /// </summary>
    public const string AdminRemoteAccessPrivateKeyUnreadableWarning =
        @"サービスアカウント（NT SERVICE\Yagura）がこの証明書の秘密鍵を読み取れません（読取権限の付与が必要です）。" +
        "このまま保存はできますが、権限を付与せずに再起動すると、リモート HTTPS の待ち受けが開かれない縮小継続になります。";

    /// <summary>
    /// 秘密鍵の読取権限の付与手順（certlm.msc の具体手順。configuration.md §6 CF-D2 と同一の手動経路——
    /// ADR-0012 決定 3 = (b)。付与後に「再確認」でその場で結果を確認できる旨を含める）。
    /// </summary>
    public const string AdminRemoteAccessPrivateKeyGrantSteps =
        @"付与手順: certlm.msc → 個人 → 証明書 → 対象の証明書を右クリック → すべてのタスク → 秘密キーの管理 → " +
        @"「NT SERVICE\Yagura」に読み取りを付与（利用者向け設定リファレンス configuration.md §6 CF-D2 と同じ手順）。" +
        "付与後に「再確認」を押すと、その場で結果を確認できます。";

    /// <summary>
    /// 現在設定されている拇印が一覧に見つからない場合の注記。{0} に拇印が入る
    /// （証明書の削除・条件不適合への変化を隠さない——ui.md §5.3 と同じ向き）。
    /// </summary>
    public const string AdminRemoteAccessThumbprintNotInListFormat =
        "現在設定されている拇印 {0} の証明書は一覧にありません（削除されたか、サーバー認証 + 秘密鍵の条件を" +
        "満たさなくなった可能性があります）。";

    /// <summary>候補 0 件の空状態の見出し（ADR-0012 受け入れ基準「空状態の案内」）。</summary>
    public const string AdminRemoteAccessEmptyTitle = "選択できる証明書が見つかりません";

    /// <summary>空状態の補足（どの条件で探したか）。</summary>
    public const string AdminRemoteAccessEmptyDescription =
        @"このサーバの証明書ストア（ローカル コンピューター\個人）に、サーバー認証（serverAuth）の用途と" +
        "秘密鍵を備えた証明書がありません。";

    /// <summary>空状態の次の行動（取り込み先と certlm.msc への言及——ADR-0012 決定 2）。</summary>
    public const string AdminRemoteAccessEmptyNextAction =
        "certlm.msc（ローカル コンピューターの証明書の管理）で「個人」ストアへ秘密鍵付きの証明書を取り込むと、" +
        "この一覧に表示されます。取り込み後に「再確認」を押してください。";

    /// <summary>証明書一覧の取得に失敗した場合の表示。{0} にエラーメッセージが入る（握り潰さない）。</summary>
    public const string AdminRemoteAccessEnumerationFailedFormat = "証明書一覧の取得に失敗しました: {0}";

    /// <summary>
    /// 反映方式の常設注記（ADR-0012 決定 6——何が・どれくらい・どう戻るか。対象 4 キーは
    /// すべて反映 = サービス再起動であり、複数の変更は 1 回の保存 + 1 回の再起動でまとめて反映できる）。
    /// </summary>
    public const string AdminRemoteAccessRestartNote =
        "この画面の設定は、保存だけでは反映されません。反映は次回のサービス再起動時で、再起動中は syslog の受信と" +
        "管理画面が停止します（停止中に送られたログは失われることがあります）。複数の項目を変更する場合は、" +
        "まとめて保存してから 1 回の再起動で反映してください。";

    /// <summary>
    /// 保存成功の通知・要約。{0} に変更されたキーの一覧が入る（ADR-0012 決定 6 の受信断明示を含む）。
    /// </summary>
    public const string AdminRemoteAccessSavedFormat =
        "リモートアクセス設定を保存しました（変更: {0}）。反映には次回サービス再起動が必要です" +
        "（再起動中は syslog の受信と管理画面が停止します）。";

    /// <summary>変更ゼロ（no-op）で保存されなかった場合の通知。</summary>
    public const string AdminRemoteAccessSavedNoChanges = "現在の設定と同じ内容のため、保存は行われませんでした。";

    /// <summary>
    /// 管理 HTTPS 画面から TLS 受信画面への相互リンクの文言（ADR-0019 決定 1——同じ証明書一覧が出る
    /// 2 画面の取り違えを防ぐため、用途を平易に書き分ける）。
    /// </summary>
    public const string AdminRemoteAccessIngestionTlsLinkNote =
        "送信元機器からのログを TLS で受信する設定は別の画面です: ";

    /// <summary>同上のリンク表示名。</summary>
    public const string AdminRemoteAccessIngestionTlsLinkText = "TLS 受信の証明書設定";

    // ---- TLS 受信の証明書設定（ADR-0019） ----

    /// <summary>TLS 受信の証明書設定画面の見出し（ADR-0019 決定 1——管理 HTTPS とは分離した画面）。</summary>
    public const string IngestionTlsTitle = "TLS 受信の証明書設定";

    /// <summary>
    /// 画面の説明文。<b>管理 HTTPS 画面との用途の書き分けを明示する</b>（ADR-0019 決定 1——
    /// 同じ証明書一覧が出る 2 画面の取り違えを防ぐ）。
    /// </summary>
    public const string IngestionTlsIntro =
        "送信元の機器から Yagura へ syslog を TLS で受信するための設定です（RFC 5425）。" +
        "証明書はこのサーバの証明書ストアから選択でき、拇印の手入力は不要です。" +
        "この設定は「機器 → Yagura」の受信用であり、ブラウザから管理画面を見るための HTTPS とは別です。";

    /// <summary>管理 HTTPS 画面への相互リンクの文言（用途の書き分け）。</summary>
    public const string IngestionTlsAdminHttpsLinkNote =
        "ブラウザから管理画面を HTTPS で見るための設定は別の画面です: ";

    /// <summary>同上のリンク表示名。</summary>
    public const string IngestionTlsAdminHttpsLinkText = "管理 UI のリモートアクセス（HTTPS）";

    /// <summary>現在の状態（永続値）カードの見出し。</summary>
    public const string IngestionTlsStatusTitle = "現在の設定（保存済み）";

    /// <summary>状態表: TLS 受信の有効/無効。</summary>
    public const string IngestionTlsStatusEnabled = "TLS 受信";

    /// <summary>状態表: 証明書拇印。</summary>
    public const string IngestionTlsStatusThumbprint = "証明書の拇印";

    /// <summary>状態表: ポート。</summary>
    public const string IngestionTlsStatusPort = "ポート";

    /// <summary>状態表: ポート未設定（既定 6514 = RFC 5425 の標準ポート）。</summary>
    public const string IngestionTlsStatusDefaultPort = "6514（既定・RFC 5425 の標準ポート）";

    /// <summary>TLS 受信の有効化スイッチ。</summary>
    public const string IngestionTlsEnabledLabel = "TLS での syslog 受信を有効にする";

    /// <summary>有効化スイッチの補足（opt-in であり平文受信とは独立であること）。</summary>
    public const string IngestionTlsEnabledNote =
        "既定では無効です。有効にしても平文の UDP / TCP 受信はそのまま継続します（別のポートで待ち受けます）。";

    /// <summary>ポート入力のラベル。</summary>
    public const string IngestionTlsPortLabel = "TLS 受信のポート";

    /// <summary>ポート入力の補助説明。</summary>
    public const string IngestionTlsPortHelp = "未指定の場合は RFC 5425 の標準ポート 6514 が使われます。";

    /// <summary>証明書選択カードの見出し。</summary>
    public const string IngestionTlsCertificatesTitle = "受信に使う証明書";

    /// <summary>証明書一覧の導入文。</summary>
    public const string IngestionTlsCertificatesIntro =
        "このサーバの証明書ストア（LocalMachine\\My）にある、サーバー認証用途で秘密鍵を持つ証明書の一覧です。";

    /// <summary>
    /// 期限切れバッジ。<b>TLS 受信では選択可能</b>（保存は警告付きで通る——ADR-0019 決定 2）。
    /// 管理 HTTPS 側は同じ状態が保存の拒否理由になる。
    /// </summary>
    public const string IngestionTlsCertExpiredBadge = "有効期間外";

    /// <summary>秘密鍵の読取不可バッジ（TLS 受信では保存の拒否理由）。</summary>
    public const string IngestionTlsCertKeyUnreadableBadge = "秘密鍵の読取権限なし";

    /// <summary>問題なしバッジ。</summary>
    public const string IngestionTlsCertOkBadge = "使用可能";

    /// <summary>
    /// 期限切れ証明書を選んだときの警告（ADR-0019 決定 2）。<b>管理 HTTPS との挙動差分の
    /// 説明を含める</b>ことが受け入れ基準。あわせて「保存しても能動通知は出続ける」ことも明示する
    /// （出続けるのは不具合ではない、と分かるようにするため）。
    /// </summary>
    public const string IngestionTlsCertExpiredWarning =
        "この証明書は有効期間外です。TLS 受信は有効期間外でも待ち受けを続けるため、この証明書のまま保存できます" +
        "（管理画面の HTTPS 設定では同じ状態は保存できません——あちらは待ち受け自体を開かないためです）。" +
        "ただし送信元の機器が証明書を検証する設定であれば接続を拒否し、そのログは届かないまま気づきにくい形で失われます。" +
        "また期限接近・使用不能の通知は、有効期間内の証明書へ差し替えるまで出続けます。";

    /// <summary>
    /// 秘密鍵が読めない証明書についての説明（TLS 受信では拒否——ADR-0019 決定 2）。
    /// 管理 HTTPS 側（警告に留める）との違いも書く。
    /// </summary>
    public const string IngestionTlsPrivateKeyUnreadableWarning =
        "この証明書の秘密鍵に、Yagura のサービスアカウントがアクセスできません。秘密鍵を読めないと TLS の" +
        "ハンドシェイクが成立せず、TLS 受信はまったく機能しないため、この証明書は選択しても保存できません。";

    /// <summary>秘密鍵の読取権限付与の手順（CF-D2 への誘導。管理 HTTPS 画面と同一手順）。</summary>
    public const string IngestionTlsPrivateKeyGrantSteps =
        "証明書スナップイン（certlm.msc）で対象の証明書を右クリック →「すべてのタスク」→「秘密キーの管理」から、" +
        "サービスアカウントへ読み取り権限を付与し、「再確認」を押してください。";

    /// <summary>一覧が空のときの見出し。</summary>
    public const string IngestionTlsEmptyTitle = "選択できる証明書がありません";

    /// <summary>一覧が空のときの説明。</summary>
    public const string IngestionTlsEmptyDescription =
        "このサーバの証明書ストア（LocalMachine\\My）に、サーバー認証用途（serverAuth）で秘密鍵を持つ証明書が" +
        "見つかりませんでした。";

    /// <summary>一覧が空のときの次の行動。</summary>
    public const string IngestionTlsEmptyNextAction =
        "証明書スナップイン（certlm.msc）から「ローカル コンピューター」の「個人」へ、秘密鍵付きの証明書を" +
        "取り込んでから「再確認」を押してください。";

    /// <summary>一覧の再取得ボタン。</summary>
    public const string IngestionTlsRefreshButton = "再確認";

    /// <summary>列挙に失敗した場合。{0} に例外メッセージが入る。</summary>
    public const string IngestionTlsEnumerationFailedFormat = "証明書の一覧を取得できませんでした: {0}";

    /// <summary>永続値の拇印が一覧に無い場合の警告。{0} に拇印が入る。</summary>
    public const string IngestionTlsThumbprintNotInListFormat =
        "現在設定されている拇印（{0}）が一覧にありません。証明書が削除されたか、サーバー認証用途・秘密鍵ありの" +
        "条件を満たさなくなった可能性があります。";

    /// <summary>
    /// 反映方式の常設注記（ADR-0019 決定 4）。<b>TLS だけでなく全受信が停止する</b>ことを明示し、
    /// 複数画面の変更をまとめて 1 回の再起動で反映するよう促す（管理 HTTPS 画面と本画面の変更は
    /// 累積されるため、2 画面 1 再起動で完結できる）。
    /// </summary>
    public const string IngestionTlsRestartNote =
        "この画面の設定は、保存だけでは反映されません。反映は次回のサービス再起動時で、再起動中は TLS だけでなく" +
        "すべての受信（UDP / TCP を含む）と管理画面が停止します（停止中に送られたログは失われることがあります）。" +
        "管理画面の HTTPS 設定など他の変更もある場合は、まとめて保存してから 1 回の再起動で反映してください。";

    /// <summary>保存成功の要約。{0} に変更されたキーの一覧が入る。</summary>
    public const string IngestionTlsSavedFormat =
        "TLS 受信の設定を保存しました（変更: {0}）。反映するにはサービスの再起動が必要です。";

    /// <summary>変更ゼロ（no-op）の通知。</summary>
    public const string IngestionTlsSavedNoChanges = "現在の設定と同じ内容のため、保存は行われませんでした。";

    /// <summary>
    /// 観測性への導線の見出し（ADR-0019 決定 5b）。
    /// 証明書差し替え + 再起動の直後に運用者が行う「全送信元が再接続できたかの確認」を、
    /// 設定画面から始められるようにする。
    /// </summary>
    public const string IngestionTlsObservabilityTitle = "受信状況の確認";

    /// <summary>同上の説明文。</summary>
    public const string IngestionTlsObservabilityNote =
        "証明書を差し替えて再起動したあとは、送信元の機器がすべて再接続できたかを確認してください。" +
        "TLS のハンドシェイク失敗は計器に、送信元ごとの最終受信時刻はダッシュボードに出ます。";

    /// <summary>観測性リンク: 送信元別の受信状況。</summary>
    public const string IngestionTlsObservabilitySourcesLinkText = "送信元別の受信状況";

    /// <summary>観測性リンク: 計器（ハンドシェイク失敗を含む）。</summary>
    public const string IngestionTlsObservabilityMetricsLinkText = "計器（ハンドシェイク失敗を含む）";

    // ---- 閲覧 UI の HTTPS 設定（ADR-0022） ----

    /// <summary>閲覧 HTTPS 設定画面の見出し（決定 3——証明書設定 3 画面の書き分け）。</summary>
    public const string ViewerHttpsTitle = "閲覧 UI の HTTPS 設定";

    /// <summary>
    /// 画面の説明文。<b>他の証明書 2 画面との用途の書き分けを明示する</b>（決定 3——
    /// 同じ証明書一覧が出る 3 画面の取り違えを防ぐ）。
    /// </summary>
    public const string ViewerHttpsIntro =
        "LAN のブラウザから閲覧画面（既定ポート 8514）を HTTPS で開けるようにする設定です。" +
        "有効化すると、閲覧ポートはそのままで平文 HTTP から HTTPS に切り替わります（平文の面は残りません）。" +
        "この設定は「LAN のブラウザ → Yagura」の閲覧用であり、管理画面のリモート HTTPS・機器からの TLS 受信とは別です。";

    /// <summary>他の証明書設定画面への相互リンクの前置き。</summary>
    public const string ViewerHttpsOtherScreensNote = "他の証明書設定:";

    /// <summary>現在の状態の見出し。</summary>
    public const string ViewerHttpsStatusTitle = "現在の設定（保存済みの値）";

    /// <summary>状態行: 有効/無効。</summary>
    public const string ViewerHttpsStatusEnabled = "閲覧 UI の HTTPS";

    /// <summary>状態行: 証明書拇印。</summary>
    public const string ViewerHttpsStatusThumbprint = "証明書（拇印）";

    /// <summary>状態行: 閲覧ポート。</summary>
    public const string ViewerHttpsStatusPort = "閲覧ポート（HTTPS 有効時も同じポート）";

    /// <summary>状態行: 閲覧ポートの既定値表示。</summary>
    public const string ViewerHttpsStatusDefaultPort = "8514（既定）";

    /// <summary>有効化スイッチのラベル。</summary>
    public const string ViewerHttpsEnabledLabel = "閲覧 UI の HTTPS を有効にする";

    /// <summary>有効化スイッチの注記（8514 同一ポート切替 = 旧 URL 断絶の予告。決定 6）。</summary>
    public const string ViewerHttpsEnabledNote =
        "有効化を反映すると、閲覧 URL は https:// に変わり、http:// の従来 URL（ブックマーク・" +
        "ショートカット・掲示用端末）は開けなくなります。反映の前に閲覧者への周知を済ませてください。";

    /// <summary>証明書一覧の見出し。</summary>
    public const string ViewerHttpsCertificatesTitle = "証明書の選択";

    /// <summary>証明書一覧の説明。</summary>
    public const string ViewerHttpsCertificatesIntro =
        "このサーバの証明書ストア（ローカルコンピューター）にあるサーバー認証用の証明書から選択します。" +
        "期限切れの証明書は選択しても保存できません（閲覧 HTTPS は期限切れでは動作せず、平文には落ちません）。";

    /// <summary>一覧更新ボタン。</summary>
    public const string ViewerHttpsRefreshButton = "一覧を更新";

    /// <summary>列挙失敗の表示（{0} = 理由）。</summary>
    public const string ViewerHttpsEnumerationFailedFormat = "証明書ストアの一覧を取得できませんでした: {0}";

    /// <summary>候補ゼロ件の見出し。</summary>
    public const string ViewerHttpsEmptyTitle = "選択できる証明書がありません";

    /// <summary>候補ゼロ件の説明。</summary>
    public const string ViewerHttpsEmptyDescription =
        "ローカルコンピューターの証明書ストア（個人）に、サーバー認証（serverAuth）の拡張キー使用法と" +
        "秘密鍵を持つ証明書が見つかりませんでした。";

    /// <summary>候補ゼロ件の次アクション（CF-D2 の主経路への誘導）。</summary>
    public const string ViewerHttpsEmptyNextAction =
        "証明書の入手方法（AD 環境での自己署名 + グループポリシー配布の手順を含む）は利用者ガイドを参照してください。";

    /// <summary>永続値の拇印が一覧に無い場合の警告（{0} = 拇印）。</summary>
    public const string ViewerHttpsThumbprintNotInListFormat =
        "設定済みの拇印 {0} に一致する証明書が一覧にありません（削除された・条件〔serverAuth EKU + 秘密鍵〕を満たさなくなった可能性）。";

    /// <summary>期限切れバッジ（閲覧 HTTPS では保存拒否——Error 表示）。</summary>
    public const string ViewerHttpsCertExpiredBadge = "期限切れ（保存不可）";

    /// <summary>秘密鍵読取不可バッジ（閲覧 HTTPS では保存拒否——Error 表示）。</summary>
    public const string ViewerHttpsCertKeyUnreadableBadge = "秘密鍵にアクセス不可（保存不可）";

    /// <summary>問題なしバッジ。</summary>
    public const string ViewerHttpsCertOkBadge = "選択可";

    /// <summary>期限切れ証明書の説明（管理 HTTPS と同じ拒否側・TLS 受信との差分の明示）。</summary>
    public const string ViewerHttpsCertExpiredNote =
        "閲覧 UI の HTTPS は期限切れ証明書では動作しません（HTTPS リスナは停止し、平文 HTTP へは落としません）。" +
        "このため保存を拒否します（TLS 受信画面が期限切れを警告付きで通すのは、受信は期限切れでも止めない設計のためで、意図的な違いです）。";

    /// <summary>秘密鍵読取不可の説明 + certlm.msc 誘導（CF-D2）。</summary>
    public const string ViewerHttpsPrivateKeyUnreadableNote =
        "秘密鍵を読めないと TLS ハンドシェイクが成立せず、閲覧 UI に誰も接続できなくなるため、保存を拒否します。" +
        "証明書スナップイン（certlm.msc）で対象の証明書を右クリック →「すべてのタスク」→「秘密キーの管理」から、" +
        "サービスアカウントへ読み取り権限を付与し、「一覧を更新」で再確認してください。";

    /// <summary>「管理リモート HTTPS と同じ証明書を使う」コピーボタン（決定 9）。</summary>
    public const string ViewerHttpsCopyFromAdminButton = "管理リモート HTTPS と同じ証明書を使う";

    /// <summary>コピー動線の注記（暗黙連動なしの明示。決定 9）。</summary>
    public const string ViewerHttpsCopyFromAdminNote =
        "管理リモート HTTPS に設定済みの拇印をこの画面の選択欄へ転写します（転写のみで、以後の連動はありません。" +
        "保存時には通常の選択と同じ検証と SAN 検査を通ります）。";

    /// <summary>SAN 検査の見出し（決定 4）。</summary>
    public const string ViewerHttpsSanTitle = "証明書のホスト名（SAN）検査";

    /// <summary>SAN 検査: 整合（{0} = カバーされたサーバ名の列挙）。断定形を避ける（委任 4 の実機検証ゲート前）。</summary>
    public const string ViewerHttpsSanSatisfiedFormat =
        "この証明書の SAN はサーバの名前（{0}）を含んでいます。これらの名前でのアクセスは、配布済みのルート証明書を信頼している端末では証明書名の不一致警告にならない見込みです。";

    /// <summary>SAN 検査: 不整合の見出し（{0} = 不足しているサーバ名の列挙）。</summary>
    public const string ViewerHttpsSanMissingFormat =
        "この証明書の SAN には、サーバの名前のうち {0} が含まれていません。これらの名前で https:// アクセスすると、ブラウザに証明書名の不一致警告が出ます（保存は可能です——実際に使う名前が SAN にあれば問題ありません）。";

    /// <summary>SAN 検査: SAN 拡張なし。</summary>
    public const string ViewerHttpsSanNoExtension =
        "この証明書は SAN（サブジェクト代替名）拡張を持ちません。現代のブラウザは SAN のみを照合するため、どの名前でアクセスしても証明書名の不一致警告になります。SAN 付きの証明書への差し替えを推奨します。";

    /// <summary>SAN 検査: 警告なしでアクセスできる URL の提示（決定 4 の文言方針）。</summary>
    public const string ViewerHttpsSanUrlListIntro = "この証明書で警告なしになる見込みの閲覧 URL:";

    /// <summary>SAN 検査の限界の注記（IP 対象外——決定 4）。</summary>
    public const string ViewerHttpsSanLimitationNote =
        "この検査はホスト名（DNS 名）のみが対象です。IP アドレスでのアクセス（例: https://192.168.1.10:8514/）は" +
        "検査対象外で、証明書に IP の記載がない限り警告が出ます。閲覧者には IP ではなく名前の URL を周知してください。";

    /// <summary>反映方式の常設注記（決定 1 の受信断レール）。</summary>
    public const string ViewerHttpsRestartNote =
        "この画面の変更は保存だけでは反映されません。反映は次回サービス再起動時で、再起動中は syslog の受信を含む" +
        "すべての機能が一時停止します。管理リモート HTTPS・TLS 受信の証明書変更がある場合は、まとめて保存してから" +
        "1 回の再起動で反映することを推奨します（未反映の変更は管理ホームの「再起動待ちの変更」に表示されます）。";

    /// <summary>保存成功（{0} = 変更キーの列挙）。</summary>
    public const string ViewerHttpsSavedFormat = "保存しました（変更: {0}）。反映にはサービス再起動が必要です。";

    /// <summary>変更なし（no-op）。</summary>
    public const string ViewerHttpsSavedNoChanges = "変更はありません（保存・監査は行われていません）。";

    /// <summary>有効化保存後の案内の見出し（決定 6）。</summary>
    public const string ViewerHttpsAfterEnableTitle = "反映後の閲覧 URL について";

    /// <summary>有効化保存後の案内 (a): 反映は再起動後（保存直後に https を試して開けないのは正常）。</summary>
    public const string ViewerHttpsAfterEnableRestartNote =
        "新しい URL が開けるのはサービス再起動後です（保存直後に https:// を試して開けないのは正常です）。";

    /// <summary>有効化保存後の案内 (b): 新 URL の提示（{0} = URL 一覧）。</summary>
    public const string ViewerHttpsAfterEnableNewUrlFormat = "反映後の閲覧 URL: {0}";

    /// <summary>有効化保存後の案内: 環境変数上書きの注記。</summary>
    public const string ViewerHttpsAfterEnablePortOverrideNote =
        "（環境変数 YAGURA_HTTP_PORT でポートを上書きしている場合は、そのポートが HTTPS になります）";

    /// <summary>有効化保存後の案内 (c): 旧 URL 断絶の周知依頼。</summary>
    public const string ViewerHttpsAfterEnableBookmarkNote =
        "http:// の従来 URL（閲覧者のブックマーク・スタートメニュー/デスクトップのショートカット・掲示用端末）は" +
        "反映後に開けなくなります。再起動の前に、新しい URL を閲覧者へ周知してください。";

    /// <summary>有効化保存後の案内 (d): メール通知が無効の場合の指摘 + 導線（決定 6）。</summary>
    public const string ViewerHttpsAfterEnableEmailNote =
        "メール通知が無効のため、証明書の期限切れが近づいたときの事前警告は Windows イベントログにしか届きません。" +
        "期限切れになると閲覧が止まるため、メール通知の設定を推奨します。";

    /// <summary>メール通知設定へのリンクテキスト。</summary>
    public const string ViewerHttpsEmailSettingsLinkText = "メール通知の設定へ";

    /// <summary>
    /// 管理ホームの常設バナー: 閲覧リスナが縮小継続で停止中（{0} = 理由。ADR-0022 決定 2 可視化③）。
    /// </summary>
    public const string AdminHomeViewerHttpsSuppressedFormat =
        "閲覧リスナ（閲覧 UI）は証明書の問題で停止しています（平文 HTTP へは落としていません）。理由: {0} " +
        "復旧（証明書の差し替え、または HTTPS の無効化）はこの管理画面から行えます: ";

    /// <summary>
    /// 閲覧 UI の事前告知バナー: HTTPS への切替が保存済み・再起動待ち（ADR-0022 決定 6）。
    /// </summary>
    public const string ViewerHttpsPendingSwitchBanner =
        "お知らせ: この閲覧画面の URL は、次回のサービス再起動後に https:// に変わります（現在の http:// の" +
        "URL・ブックマークは開けなくなります）。新しい URL は管理者にご確認のうえ、ブックマークの更新を" +
        "お願いします。";

    // ---- 初期セットアップウィザード（configuration.md §3〜§7。M8-4 骨格） ----

    /// <summary>ステップ: 受信設定。</summary>
    public const string SetupStepReceptionTitle = "受信設定";

    /// <summary>ステップ: 閲覧と管理。</summary>
    public const string SetupStepViewerAccessTitle = "閲覧と管理";

    /// <summary>ステップ: ログを保存しておく期間（用語対応表: 保持期間）。</summary>
    public const string SetupStepRetentionTitle = "ログを保存しておく期間";

    /// <summary>ステップ: 確認。</summary>
    public const string SetupStepReviewTitle = "確認";

    /// <summary>ステップ確定ボタン。</summary>
    public const string WizardConfirmStep = "この内容で次へ";

    /// <summary>設定の適用ボタン。</summary>
    public const string WizardApply = "設定を保存する";

    /// <summary>再開位置の明示（configuration.md §7「どこから再開しているか」。{0} にステップ名）。</summary>
    public const string WizardResumeNoticeFormat = "「{0}」から再開しています。確定済みの内容は保存されています。";

    /// <summary>適用完了。</summary>
    public const string WizardApplied = "設定を保存しました。";

    /// <summary>二重適用の抑止結果（冪等トークンによる再送検出。configuration.md §7）。</summary>
    public const string WizardAlreadyApplied = "この操作は既に適用済みです（二重適用は行われていません）。";

    /// <summary>
    /// 楽観競合の検出結果（configuration.md §3——上書きせずに再読み込みを促す）。
    /// </summary>
    public const string WizardConflict =
        "設定ファイルがほかの手段（手編集など）で変更されていたため、保存を中止しました。" +
        "内容を確認のうえ、確認ステップからやり直してください。";

    /// <summary>冪等トークン不一致（期限切れ・別セッション）。</summary>
    public const string WizardInvalidToken = "操作の有効期限が切れています。確認ステップからやり直してください。";

    /// <summary>
    /// 適用完了画面からの再編集開始ボタン（適用後の画面を行き止まりにしない。
    /// 現在の設定値を種にウィザードを再開する）。
    /// </summary>
    public const string WizardBeginReconfiguration = "設定を変更する";

    /// <summary>ウィザードの前ステップへ戻るボタン（表示の移動——確定の取り消しではない）。</summary>
    public const string WizardBack = "戻る";

    /// <summary>反映方式の表示（configuration.md §3・ui.md §5.4）: 即時反映。</summary>
    public const string ApplyEffectImmediate = "変更はすぐに反映されます";

    /// <summary>反映方式の表示: リスナ再構成（接続の瞬断あり）。</summary>
    public const string ApplyEffectListenerReconfiguration = "反映時に受信の接続が一時的に切れます";

    /// <summary>反映方式の表示: サービス再起動が必要。</summary>
    public const string ApplyEffectRestartRequired = "反映にはサービスの再起動が必要です（再起動中は受信できません）";

    // ---- 本番昇格ウィザード（database.md §6.1） ----

    /// <summary>接続の入力方式: 項目で入力（既定——database.md §6.1）。</summary>
    public const string PromotionInputModeForm = "項目で入力（推奨）";

    /// <summary>接続の入力方式: 接続文字列の直接入力（上級者向け）。</summary>
    public const string PromotionInputModeRaw = "接続文字列を直接入力（上級者向け）";

    /// <summary>サーバ名の入力ラベル。</summary>
    public const string PromotionServerNameLabel = "サーバ名";

    /// <summary>サーバ名の入力例（名前付きインスタンス・ポート併記の形を示す）。</summary>
    public const string PromotionServerNamePlaceholder = @"例: SV01\SQLEXPRESS または 10.0.0.180,1433";

    /// <summary>データベース名の入力ラベル。</summary>
    public const string PromotionDatabaseNameLabel = "データベース名";

    /// <summary>認証方式の選択ラベル（用語対応表 ui.md §7.2）。</summary>
    public const string PromotionAuthModeLabel = "認証方式";

    /// <summary>認証方式: Windows 統合認証（既定 = database.md §5.1 の第一推奨）。</summary>
    public const string PromotionAuthModeWindows = "Windows 統合認証（サービスのアカウントで接続）";

    /// <summary>認証方式: SQL Server 認証。</summary>
    public const string PromotionAuthModeSql = "SQL Server 認証（ユーザー名とパスワードで接続）";

    /// <summary>
    /// Windows 統合認証の接続アカウントの明示（{0} = サービス実行アカウント名。SQL Server 側に
    /// どの名前で見えるかを推測させない——database.md §6.1）。
    /// </summary>
    public const string PromotionWindowsAccountNoteFormat =
        "接続に使うアカウント: {0}（SQL Server 側にはこの名前のログインが必要です）";

    /// <summary>ユーザー名の入力ラベル（SQL Server 認証）。</summary>
    public const string PromotionUserNameLabel = "ユーザー名";

    /// <summary>パスワードの入力ラベル（SQL Server 認証。マスク表示）。</summary>
    public const string PromotionPasswordLabel = "パスワード";

    /// <summary>パスワードの取り扱いの説明（configuration.md §5・§2 の統治を利用者の言葉で）。</summary>
    public const string PromotionPasswordHelp =
        "パスワードはこの欄でのみ入力します。切り替えの適用時に DPAPI で暗号化して保存され、" +
        "接続文字列の組み立てだけに使われます。画面や記録に平文では残りません。";

    /// <summary>サーバ証明書の信頼（TrustServerCertificate。用語対応表 ui.md §7.2）。</summary>
    public const string PromotionTrustServerCertificateLabel = "サーバ証明書を信頼する";

    /// <summary>
    /// サーバ証明書の信頼の説明（失敗前から読める常設の説明——database.md §6.1。
    /// なりすまし検知が行われない残存リスクを含む）。
    /// </summary>
    public const string PromotionTrustServerCertificateHelp =
        "証明書の検証を省略します（通信の暗号化は維持されます）。なりすまし検知は行われないため、" +
        "自己署名証明書を使う閉域環境向けの選択です。";

    /// <summary>接続文字列の入力ラベル（直接入力方式）。</summary>
    public const string PromotionConnectionStringLabel = "SQL Server への接続文字列";

    /// <summary>直接入力方式の注意（パスワード系キーの拒否——database.md §6.1）。</summary>
    public const string PromotionRawConnectionStringHelp =
        "パスワードは接続文字列に書かず、下のパスワード欄に入力してください（Password / Pwd キーはエラーになります）。";

    /// <summary>パスワードの取り扱いの説明（configuration.md §5 の統治を利用者の言葉で）。</summary>
    public const string PromotionCredentialHandlingNote =
        "パスワードはこのウィザードの実行中だけサーバのメモリ上に保持され、完了または中断で破棄されます。" +
        "15 分間操作がない場合も破棄され、再開時に再入力が必要です（サーバ名などの入力内容は保持されます）。";

    /// <summary>接続検証ボタン（database.md §6.1 準備フェーズ）。</summary>
    public const string PromotionValidateConnection = "接続を検証する";

    /// <summary>接続検証成功。</summary>
    public const string PromotionValidationSucceeded = "SQL Server への接続を確認しました。";

    /// <summary>パスワードの再入力要求（無操作タイムアウト後の再開。configuration.md §5）。</summary>
    public const string PromotionCredentialReentryRequired =
        "操作の間隔が空いたため、パスワードを破棄しました。再入力してください（確定済みの選択は保存されています）。";

    /// <summary>
    /// 検証失敗の案内: サーバ証明書が信頼されない（database.md §6.1 分類①。次の一手 =
    /// 「サーバ証明書を信頼する」の有効化）。
    /// </summary>
    public const string PromotionFailureCertificateGuidance =
        "SQL Server の証明書が信頼されていないため接続できませんでした。自己署名証明書を使っている場合は" +
        "「サーバ証明書を信頼する」を有効にして、もう一度検証してください。";

    /// <summary>検証失敗の案内: サーバへ到達できない（分類②。修復 SQL なし）。</summary>
    public const string PromotionFailureUnreachableGuidance =
        "SQL Server へ到達できませんでした。サーバ名・ポート・ファイアウォールの設定を確認してください。";

    /// <summary>
    /// 検証失敗の案内: ログイン失敗（分類③。18456 は誤パスワードでも DB 不在でも返るため
    /// 条件付きの案内——database.md §6.1）。
    /// </summary>
    public const string PromotionFailureLoginGuidance =
        "SQL Server にログインできませんでした。まずユーザー名とパスワード（Windows 統合認証の場合は" +
        "接続に使うアカウント）を確認してください。ログインが未作成の場合は、下の SQL で作成できます。";

    /// <summary>検証失敗の案内: データベース不在（分類④）。</summary>
    public const string PromotionFailureDatabaseNotFoundGuidance =
        "指定したデータベースを開けませんでした。データベースが未作成の場合は、下の SQL で作成できます。";

    /// <summary>検証失敗の案内: 分類できない失敗（生メッセージ + 汎用案内のみ——database.md §6.1）。</summary>
    public const string PromotionFailureUnclassifiedGuidance =
        "接続できませんでした。次のエラーメッセージを確認してください。";

    /// <summary>
    /// 修復 SQL のブロック見出し（用語対応表 ui.md §7.2——このサーバは実行しない・
    /// SQL Server 側で実行する旨を見出しで伝える）。
    /// </summary>
    public const string PromotionRemediationSqlLabel =
        "解決するための SQL（このサーバは実行しません。SSMS 等で SQL Server 側で実行してください）";

    /// <summary>退避先フォルダの入力ラベル（database.md §6.1——退避の選択時は必須）。</summary>
    public const string PromotionEvacuationDirectoryLabel = "退避先のフォルダ";

    /// <summary>退避先フォルダの入力例。</summary>
    public const string PromotionEvacuationDirectoryPlaceholder = @"例: D:\Backup\Yagura";

    /// <summary>退避の選択の確定ボタン（退避先の入力とセットで確定する）。</summary>
    public const string PromotionConfirmEvacuation = "この退避先で「退避」を選択する";

    /// <summary>
    /// 切替確定前の予告（database.md §6.1 の委任を ui.md §5.4 が確定した文言）。
    /// </summary>
    public const string PromotionSwitchWarning =
        "切り替えると、これまでに保存したログは移行機能の提供まで画面から参照できなくなります。" +
        "あとで参照する可能性がある場合は「退避」を選んでください。";

    /// <summary>
    /// 旧・組み込み DB ファイルの処分: 退避。
    /// **「移動して保管する」とは書かない**（Issue #502）——現行の版では実ファイルの移動は
    /// 行われないため、動作を約束する文言にすると利用者は「退避された」と誤解する。
    /// </summary>
    public const string PromotionDisposalEvacuate = "退避（あとで参照できるよう保管する）";

    /// <summary>旧・組み込み DB ファイルの処分: 削除。</summary>
    public const string PromotionDisposalDelete = "削除（あとで参照しない）";

    /// <summary>
    /// 処分が未実装であることの明示（Issue #502）。**選択する前**に出す——
    /// 切替完了後にだけ伝えると、退避先を入力した利用者は「退避済み」と思い込んだまま
    /// 旧ファイルを放置する（または元の場所を探さない）ことになる。
    /// </summary>
    public const string PromotionDisposalManualNotice =
        "現在の版では、ここでの選択は記録されるだけで、旧データベースファイルの移動・削除は" +
        "自動では行われません。切り替え後も、旧ファイルは元の場所に残ります。";

    /// <summary>処分が未実装であることの補足（次に取る手順）。</summary>
    public const string PromotionDisposalManualSupplement =
        "選択した内容は監査記録に残ります。実際の移動・削除は、切り替えの完了後に手動で行ってください" +
        "（手順は運用ガイドの「昇格後に旧データベースファイルを処分する」を参照）。";

    /// <summary>切替実行ボタン（破壊的操作。確認ダイアログ必須——ui.md §3.1）。</summary>
    public const string PromotionExecute = "切り替えを実行する";

    /// <summary>切替実行の確認ダイアログの見出し。</summary>
    public const string PromotionExecuteConfirmTitle = "保存先を SQL Server に切り替えます";

    /// <summary>切替実行の確認ダイアログの確認ボタン。</summary>
    public const string PromotionExecuteConfirmAction = "切り替える";

    // ---- circuit 管理画面（security.md §2.2。M8-4） ----

    /// <summary>一覧列: 接続元。</summary>
    public const string CircuitColumnRemote = "接続元";

    /// <summary>
    /// 一覧列: 接続の種別（管理 / 閲覧）。開発用語「リスナ」を画面に出さない（ui.md §7.1）。
    /// </summary>
    public const string CircuitColumnListener = "種別";

    /// <summary>一覧列: 確立時刻。</summary>
    public const string CircuitColumnOpenedAt = "接続した時刻";

    /// <summary>一覧列: 最終活動時刻。</summary>
    public const string CircuitColumnLastActivity = "最後に操作した時刻";

    /// <summary>リスナ表示: 管理。</summary>
    public const string CircuitListenerAdmin = "管理";

    /// <summary>リスナ表示: 閲覧（帰属不明も閲覧として表示する——安全側の扱いと揃える）。</summary>
    public const string CircuitListenerViewer = "閲覧";

    /// <summary>切断ボタン。</summary>
    public const string CircuitDisconnect = "切断";

    /// <summary>切断の確認ダイアログの見出し。</summary>
    public const string CircuitDisconnectConfirmTitle = "画面とサーバの接続を切断します";

    /// <summary>切断の確認ダイアログの要約（何が起きるか——ui.md §3.1 確認ダイアログ規約）。</summary>
    public const string CircuitDisconnectConfirmSummary =
        "選択した閲覧者の画面は接続終了の案内に切り替わります。ログの受信には影響しません。";

    /// <summary>切断の確認ダイアログの確認ボタン。</summary>
    public const string CircuitDisconnectConfirmAction = "切断する";

    /// <summary>切断要求の受理。</summary>
    public const string CircuitDisconnectAccepted = "切断しました。";

    /// <summary>切断要求の不成立（対象が既に終了している等）。</summary>
    public const string CircuitDisconnectNotAccepted =
        "切断できませんでした。対象の接続が既に終了しているか、切断を受け付けられない状態です。";

    // ---- メール通知（ADR-0017） ----

    public const string EmailNotificationTitle = "メール通知";

    public const string EmailNotificationIntro =
        "スプールの上限接近・証明書の期限接近・認証攻撃の予兆といった運用上の警告を、メールで受け取ります。"
        + "既定では無効です。監視基盤をお持ちの場合は、Windows イベントログ（ソース: Yagura）を"
        + "そのまま監視するほうが確実です。";

    /// <summary>メールが正本ではないことの常設注記（決定 5 の at-most-once を隠さない）。</summary>
    public const string EmailNotificationAtMostOnceNote =
        "通知メールは配送を保証しません（送信できなかった通知は 1 回だけ再試行し、それでも失敗した場合は破棄します）。"
        + "「メールが来ない ＝ 正常」とは限りません。すべての事象は Windows イベントログに記録され、そちらが正本です。"
        + "チャネルが静かに壊れていないかは、下の「送信状況」で確認してください。";

    public const string EmailNotificationEnabledLabel = "メール通知を有効にする";

    public const string EmailNotificationFromLabel = "差出人アドレス";
    public const string EmailNotificationFromHelp =
        "SMTP サーバが送信を許可しているアドレスを指定してください（例: yagura@example.co.jp）。";

    public const string EmailNotificationToLabel = "宛先アドレス";
    public const string EmailNotificationToHelp =
        "1 行に 1 件で入力してください。すべての宛先に同じ内容が送られます（宛先ごとの振り分けはできません）。";

    public const string EmailNotificationSmtpTitle = "SMTP サーバ";
    public const string EmailNotificationSmtpHostLabel = "ホスト名";
    public const string EmailNotificationSmtpPortLabel = "ポート";
    public const string EmailNotificationSmtpPortHelp = "既定は 25 です。STARTTLS を使う場合は 587 が一般的です。";

    public const string EmailNotificationSecurityLabel = "暗号化（STARTTLS）";
    public const string EmailNotificationSecurityNone = "なし（平文で送信する）";
    public const string EmailNotificationSecurityAuto = "自動（対応していれば暗号化する）";
    public const string EmailNotificationSecurityRequired = "必須（暗号化できなければ送信しない）";
    public const string EmailNotificationSecurityHelp =
        "「自動」は、サーバが暗号化に対応していない場合は平文のまま送信します。"
        + "経路に信頼できない区間がある場合は「必須」を選んでください。";

    public const string EmailNotificationAuthTitle = "SMTP 認証（任意）";
    public const string EmailNotificationAuthHelp =
        "ユーザー名とパスワードの両方を入力したときだけ認証します。片方だけでは保存できません"
        + "（認証なしの送信に黙って切り替わることを避けるためです）。";

    public const string EmailNotificationUsernameLabel = "ユーザー名";
    public const string EmailNotificationPasswordLabel = "パスワード";
    public const string EmailNotificationPasswordHelpConfigured =
        "保存済みです。変更する場合のみ入力してください（空欄のままなら現在の値を維持します）。";
    public const string EmailNotificationPasswordHelpUnset =
        "入力した値は暗号化して保存します（このサーバでのみ復号できます）。画面に再表示することはありません。";

    /// <summary>
    /// 保存済みパスワードの明示的な削除。「空欄 = 変更しない」に固定した
    /// 帰結として、削除には専用の口が要る——これがないと SMTP 認証をやめる操作ができない。
    /// </summary>
    public const string EmailNotificationClearPasswordLabel = "保存済みのパスワードを削除する";
    public const string EmailNotificationClearPasswordHelp =
        "SMTP 認証をやめる場合は、ユーザー名を空にしたうえでこれを有効にして適用してください。";

    /// <summary>決定 3 の能動警告。STARTTLS ストリップで漏れるのが「資格情報」であることを明示する。</summary>
    public const string EmailNotificationPlaintextCredentialWarning =
        "パスワードを設定していますが、暗号化が「必須」になっていません。"
        + "経路上で暗号化が剥がされた場合、漏れるのは通知の内容ではなく SMTP の資格情報です"
        + "（多くの環境では AD のアカウントと同じものです）。暗号化を「必須」にすることを強く推奨します。";

    public const string EmailNotificationTestTitle = "テスト送信";
    public const string EmailNotificationTestIntro =
        "この画面に入力中の値で 1 通だけ送信します（保存前でも試せます）。"
        + "テスト送信は通知の送信数の上限を消費しません。";
    public const string EmailNotificationTestButton = "テスト送信する";
    public const string EmailNotificationTestSending = "送信中…";
    public const string EmailNotificationTestCancelButton = "中止する";

    /// <summary>テスト送信で保存済みパスワードが使われることの明示（決定 8）。</summary>
    public const string EmailNotificationTestUsesStoredPassword =
        "パスワード欄が空欄のため、保存済みのパスワードを使って送信します。";

    public const string EmailNotificationTestRejectedRecipientsFormat =
        "次の宛先はサーバに受理されませんでした: {0}";

    public const string EmailNotificationHealthTitle = "送信状況";
    public const string EmailNotificationHealthLastSuccess = "最終送信成功";
    public const string EmailNotificationHealthLastFailure = "直近の失敗";
    public const string EmailNotificationHealthQueueDepth = "送信待ち";
    /// <summary>
    /// キュー溢れ・流量上限時の押しのけ・再試行の投入不能の 3 経路の合計
    /// （「キュー溢れ」と限定して表示しない。内訳は EmailNotificationQueue.DroppedCount 参照）。
    /// </summary>
    public const string EmailNotificationHealthDropped = "送信されず破棄した通知";
    public const string EmailNotificationHealthSuppressed = "抑制した通知";
    public const string EmailNotificationHealthNever = "なし";
    public const string EmailNotificationHealthCountFormat = "{0} 件";

    /// <summary>抑制の内訳（回数だけでは「何が届かなかったか」が分からない——決定 5）。</summary>
    public const string EmailNotificationHealthSuppressedBreakdownTitle = "抑制された事象の内訳（イベント ID 別）";
    public const string EmailNotificationHealthSuppressedNote =
        "同じ事象の通知は 1 時間に 1 通までに畳まれます。全体では 1 時間に 10 通までです"
        + "（エラーはこの上限の対象外です）。抑制された事象も Windows イベントログには記録されています。";

    /// <summary>「有効にしたつもりで送られていない」状態——画面上で最も目立たせる（決定 2）。</summary>
    public const string EmailNotificationDisabledByInvalidConfiguration =
        "メール通知は有効になっていますが、設定に不備があるため送信されていません。"
        + "この画面で設定を保存し直すと、不備の内容がその場で表示されます。";

    public const string EmailNotificationSavedNoChanges = "変更はありませんでした（保存していません）。";
    public const string EmailNotificationSavedFormat = "メール通知の設定を保存しました（変更: {0}）。次回の通知から反映されます。";

    /// <summary>無関係キーの不正で設定ファイル全体の検証が失敗している状態の表示。</summary>
    public const string EmailNotificationConfigurationFileErrorIntro =
        "設定ファイルにメール通知とは別の問題があり、実効状態を判定できません。"
        + "この画面での保存はできますが、送信側への反映は問題の解消後の再読み込みまで見送られます。検証エラー:";

    public const string EmailNotificationSaveFailedFormat = "保存できませんでした: {0}";

    // ------------------------------------------------------------------
    // 送信元の途絶検知（ADR-0018。/admin/source-silence）
    // ------------------------------------------------------------------

    public const string SourceSilenceTitle = "送信元の途絶検知";
    public const string SourceSilenceIntro =
        "ウォッチリストに登録した送信元からの受信が、指定した時間を超えて途絶えると警告します"
        + "（Windows イベントログ。メール通知を有効にしていればメールでも届きます）。"
        + "登録していない送信元は対象になりません。";

    public const string SourceSilenceListTitle = "ウォッチリスト";
    public const string SourceSilenceListEmpty =
        "まだ登録がありません。下の「登録」から、受信実績のある送信元を選んで追加してください。";
    public const string SourceSilenceColumnAddress = "送信元アドレス";
    public const string SourceSilenceColumnLabel = "表示名";
    public const string SourceSilenceColumnThreshold = "閾値";
    public const string SourceSilenceColumnState = "状態";
    public const string SourceSilenceColumnActions = "操作";
    public const string SourceSilenceStateSilent = "途絶中";
    public const string SourceSilenceStateWatching = "監視中";

    /// <summary>保存済みだが稼働状態に現れない（設定不備でエントリ単位無効化された）エントリの表示。</summary>
    public const string SourceSilenceStateInactive = "無効（設定を確認）";

    /// <summary>未保存の追加・変更があるエントリの表示（適用までは監視されない）。</summary>
    public const string SourceSilenceStatePending = "未適用";

    /// <summary>閾値が手編集の省略により既定値で補完されているエントリの表示（決定 1 の識別表示）。</summary>
    public const string SourceSilenceThresholdDefaultedFormat = "{0}（既定値で補完）";

    public const string SourceSilenceEditButton = "編集";
    public const string SourceSilenceDeleteButton = "削除";

    public const string SourceSilenceAddTitle = "登録";
    public const string SourceSilenceAddIntro =
        "受信実績のある送信元から選ぶのが確実です（アドレスの打ち間違いは「登録したのに検知されない」"
        + "という気づきにくい失敗になります）。まだ受信のない送信元を先回りで登録する場合のみ手入力してください。";
    public const string SourceSilenceCandidateLabel = "受信実績のある送信元から選択";
    public const string SourceSilenceCandidateItemFormat = "{0}（最終受信 {1}・累計 {2} 件）";
    public const string SourceSilenceCandidateRegisteredSuffix = "（登録済み）";
    public const string SourceSilenceManualAddressLabel = "送信元アドレス（手入力）";
    public const string SourceSilenceManualAddressHelp =
        "IPv4 / IPv6 アドレスを入力します。先回り登録した送信元は、登録時点から閾値の時間だけ受信がないと警告されます"
        + "——機器側の設定漏れや経路の未開通の検出に使えます。";
    public const string SourceSilenceLabelLabel = "表示名（任意）";
    public const string SourceSilenceLabelHelp = "どの装置かを自分の言葉で残せます（警告と一覧に表示されます）。";
    public const string SourceSilenceThresholdLabel = "閾値";
    public const string SourceSilenceThresholdHelp =
        "この時間を超えて受信がないと警告します。短すぎると装置の送信間隔やジッタで誤検知になります"
        + "（例: 24 時間 = 1 日 1 回でもログを送る装置向け）。"
        + "既存エントリの編集で空にすると、既定値の明示指定に変えず「省略のまま」を保ちます。";
    public const string SourceSilenceUnitMinutes = "分";
    public const string SourceSilenceUnitHours = "時間";
    public const string SourceSilenceUnitDays = "日";
    public const string SourceSilenceAddButton = "一覧に追加";
    public const string SourceSilenceUpdateButton = "一覧を更新";

    public const string SourceSilenceApplyButton = "適用する";
    public const string SourceSilencePendingChangesNote =
        "一覧への追加・編集・削除は「適用する」を押すまで保存されません。";
    public const string SourceSilenceSavedFormat = "ウォッチリストを保存しました（追加 {0} 件・削除 {1} 件・変更 {2} 件）。再起動なしで反映されます。";
    public const string SourceSilenceSavedNoChanges = "変更はありませんでした（保存していません）。";
    public const string SourceSilenceLimitFormat = "登録は {0} 件までです（現在 {1} 件）。";

    /// <summary>ダッシュボード UI-4: ウォッチリスト登録済みの送信元のマーク（ADR-0018 決定 4）。</summary>
    public const string SourceWatchRegisteredChip = "監視中";

    /// <summary>ダッシュボード UI-4: 途絶中と判定されている送信元の強調（ADR-0018 決定 4）。</summary>
    public const string SourceWatchSilentChip = "途絶中";
}
