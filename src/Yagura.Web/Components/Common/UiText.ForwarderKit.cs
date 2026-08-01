namespace Yagura.Web.Components.Common;

public static partial class UiText
{
    // ---- フォワーダキット生成画面（ADR-0008。/admin/forwarder-kit） ----

    /// <summary>画面見出し・ナビリンク文言。</summary>
    public const string ForwarderKitTitle = "フォワーダ配布キットの生成";

    /// <summary>画面の説明文（何をする画面か）。</summary>
    public const string ForwarderKitIntro =
        "Windows イベントログを転送する Fluent Bit 配布キットを、このサーバの宛先を設定済みの状態で生成します。" +
        "生成した ZIP は install.ps1 をパラメータなしで実行できます。";

    /// <summary>宛先選択の見出し。</summary>
    public const string ForwarderKitDestinationTitle = "宛先";

    /// <summary>
    /// 候補への既定選択なしの注記（ADR-0008 設計条件 1——到達可能性の判断責任は管理者に残る）。
    /// </summary>
    public const string ForwarderKitDestinationNote =
        "既定の選択はありません。端末から到達できるアドレスかどうかの判断は管理者に委ねられます" +
        "（ループバック・リンクローカル・無効な NIC は候補から除外済み）。";

    /// <summary>候補が 1 件もない場合の案内。</summary>
    public const string ForwarderKitNoCandidates = "候補となるアドレスが見つかりませんでした。手入力で指定してください。";

    /// <summary>手入力の選択肢ラベル。</summary>
    public const string ForwarderKitManualEntryOption = "手入力";

    /// <summary>手入力欄のラベル。</summary>
    public const string ForwarderKitManualEntryLabel = "宛先ホスト（IP アドレスまたはホスト名）";

    /// <summary>ポート入力のラベル。</summary>
    public const string ForwarderKitPortLabel = "ポート";

    // ---- 転送方式（Udp（既定）/ Tcp。TLS 送信はキットから除外） ----

    /// <summary>転送方式選択の見出し。</summary>
    public const string ForwarderKitModeTitle = "転送方式";

    /// <summary>転送方式選択の説明。</summary>
    public const string ForwarderKitModeNote =
        "UDP は既定で単純ですが、MTU を超えるイベントは断片化により失われることがあります。" +
        "TCP は断片化損失を避けられますが、注意点があります（選択時に表示）。";

    /// <summary>転送方式: UDP（既定）。</summary>
    public const string ForwarderKitModeUdp = "UDP（既定）";

    /// <summary>転送方式: TCP。</summary>
    public const string ForwarderKitModeTcp = "TCP";

    /// <summary>TCP 選択時の注記（RFC 6587 octet-counting 非対応の制約）。</summary>
    public const string ForwarderKitModeTcpNote =
        "Fluent Bit の out_syslog は TCP で RFC 6587 の octet-counting に対応していません（LF 区切り）。" +
        "複数行を含むイベント本文（Security 監査ログ等）が複数レコードに分かれて届く場合があります。";

    /// <summary>チャネル選択の見出し。</summary>
    public const string ForwarderKitChannelsTitle = "収集チャネル";

    /// <summary>チャネル: System。</summary>
    public const string ForwarderKitChannelSystem = "System";

    /// <summary>チャネル: Application。</summary>
    public const string ForwarderKitChannelApplication = "Application";

    /// <summary>チャネル: Security。</summary>
    public const string ForwarderKitChannelSecurity = "Security";

    /// <summary>
    /// Security チャネル有効化時の警告（ADR-0008 設計条件 2——機微情報・量の注意）。
    /// </summary>
    public const string ForwarderKitSecurityChannelWarning =
        "Security チャネルは機微情報を含み、イベント量も多くなります。組織のポリシーで明示的に判断してから有効化してください。";

    /// <summary>生成される ZIP の内容一覧の見出し。</summary>
    public const string ForwarderKitContentsTitle = "生成される ZIP の内容";

    /// <summary>検証済み Fluent Bit 版の表示（{0} に版番号が入る）。</summary>
    public const string ForwarderKitVerifiedVersionFormat = "検証済み Fluent Bit 版: {0}";

    /// <summary>MSI 非同梱の注記。</summary>
    public const string ForwarderKitMsiNotIncludedNote =
        "MSI は含まれません。README の手順に従って packages.fluentbit.io から取得し、SHA256 で検証してください。";

    /// <summary>生成ボタン。</summary>
    public const string ForwarderKitGenerateButton = "キットを生成してダウンロード";

    /// <summary>宛先未選択時のエラー。</summary>
    public const string ForwarderKitErrorDestinationRequired = "宛先を選択または入力してください。";

    /// <summary>宛先の文字種エラー。</summary>
    public const string ForwarderKitErrorDestinationInvalid = "宛先に使える文字は英数字・ピリオド・ハイフン・コロンのみです。";

    /// <summary>ポート範囲エラー。</summary>
    public const string ForwarderKitErrorPortRange = "ポートは 1〜65535 の範囲で指定してください。";

    /// <summary>チャネル未選択エラー。</summary>
    public const string ForwarderKitErrorChannelsRequired = "収集チャネルを 1 つ以上選択してください。";

    // ---- MSI オプトイン同梱（ADR-0008 設計条件 9） ----

    /// <summary>MSI 同梱セクションの見出し。</summary>
    public const string ForwarderKitMsiSectionTitle = "MSI の同梱（任意）";

    /// <summary>配置フォルダのフルパス表示の形式。{0} にフルパスが入る。</summary>
    public const string ForwarderKitMsiFolderPathFormat = "配置フォルダ: {0}";

    // ---- 収集対象端末のアーキ選択（ADR-0009 決定 7） ----

    /// <summary>収集対象端末のアーキ選択の見出し。</summary>
    public const string ForwarderKitMsiArchitectureTitle = "収集対象端末のアーキテクチャ";

    /// <summary>アーキ選択の説明（MSI 同梱時のみ意味を持つ——構成の残り（宛先・チャネル）は共通）。</summary>
    public const string ForwarderKitMsiArchitectureNote =
        "同梱する MSI は、この配布キットの導入先端末のアーキテクチャに合わせて選んでください。迷ったら x64 を選んでください。";

    /// <summary>アーキ選択肢: x64。</summary>
    public const string ForwarderKitMsiArchitectureX64 = "x64（対応・既定）";

    /// <summary>アーキ選択肢: ARM64。</summary>
    public const string ForwarderKitMsiArchitectureArm64 = "ARM64（試験的。Windows 11 on Arm 等）";

    /// <summary>MSI 未検出時の案内の形式。{0} に期待ファイル名パターンが入る。</summary>
    public const string ForwarderKitMsiNotFoundFormat =
        "MSI 未検出。ここに {0} を配置すると、生成する ZIP に MSI を同梱できます（任意）。";

    /// <summary>MSI 同梱チェックボックスのラベル。</summary>
    public const string ForwarderKitMsiIncludeCheckbox = "MSI を同梱する";

    /// <summary>検出 MSI のファイル名表示の形式。{0} にファイル名が入る。</summary>
    public const string ForwarderKitMsiDetectedFileNameFormat = "検出したファイル: {0}";

    /// <summary>検出 MSI の版表示の形式。{0} に版が入る（取得不能時は不明）。</summary>
    public const string ForwarderKitMsiDetectedVersionFormat = "版: {0}";

    /// <summary>版取得不能時の表示。</summary>
    public const string ForwarderKitMsiVersionUnknown = "不明（ファイル名から推定した値を補助的に使用）";

    /// <summary>検出 MSI の SHA256 表示の形式。{0} にハッシュ値が入る。</summary>
    public const string ForwarderKitMsiSha256Format = "SHA256: {0}";

    /// <summary>公式ハッシュとの照合結果: 一致。</summary>
    public const string ForwarderKitMsiOfficialHashMatch = "公式配布 SHA256 と一致しました。";

    /// <summary>公式ハッシュとの照合結果: 不一致。</summary>
    public const string ForwarderKitMsiOfficialHashMismatch =
        "公式配布 SHA256 と一致しませんでした。取得元・改ざんの有無を確認してください。";

    /// <summary>公式ハッシュとの照合結果: 未確認（Yagura に公式ハッシュ未設定）。</summary>
    public const string ForwarderKitMsiOfficialHashUnverified =
        "公式配布 SHA256 との照合は未実施です（Yagura に公式ハッシュが未設定のため）。";

    /// <summary>版不一致の警告の形式。{0} に検出版、{1} に検証済み版が入る。</summary>
    public const string ForwarderKitMsiVersionMismatchWarningFormat =
        "検出した MSI の版（{0}）は検証済み版（{1}）と異なります。動作未検証の組み合わせになる可能性があります。";

    /// <summary>版不一致時の二段階確認チェックボックスのラベル（設計条件 9）。</summary>
    public const string ForwarderKitMsiVersionMismatchAcknowledge =
        "版が異なることを理解した上で、この MSI を同梱します";

    /// <summary>複数 MSI 検出時のエラー見出し。</summary>
    public const string ForwarderKitMsiMultipleErrorTitle = "複数の MSI が見つかりました。1 つだけ残してください。";

    /// <summary>複数 MSI 検出時、検出一覧の見出し。</summary>
    public const string ForwarderKitMsiMultipleListTitle = "検出したファイル:";

    /// <summary>同梱選択時の ZIP サイズ予告。</summary>
    public const string ForwarderKitMsiSizeNotice = "MSI を同梱すると、生成する ZIP のサイズは 20 MB を超える見込みです。";

    /// <summary>版不一致の確認未了エラー（生成ボタン押下時）。</summary>
    public const string ForwarderKitErrorMsiVersionMismatchNotAcknowledged =
        "MSI の版が検証済み版と異なります。同梱するには確認チェックを入れてください。";

    // ---- フォワーダ MSI アップロード（ADR-0020。配置経路 (b)） ----

    public const string ForwarderMsiUploadSectionTitle = "MSI のアップロード（管理画面から配置）";

    /// <summary>機能が無効な構成での案内（沈黙にしない——ADR-0020 決定 1）。</summary>
    public const string ForwarderMsiUploadDisabledTitle =
        "管理画面からの MSI アップロードは、この構成では無効です。";

    public const string ForwarderMsiUploadDisabledIntro =
        "有効化には次の設定がすべて必要です（全端末に配布される MSI の書き込み口につながる操作のため、"
        + "実際にサインインした管理者だけが実行できます——サインインの手段が構成されていない構成では"
        + "有効化できません）:";

    public const string ForwarderMsiUploadConditionAuth =
        "管理 UI 認証の有効化（Windows 統合認証またはアプリ独自 ID/パスワード）";

    public const string ForwarderMsiUploadConditionOptIn =
        "アップロード機能そのものの有効化（Admin:ForwarderKit:MsiUpload:Enabled）";

    // ---- アップロード操作単位の実認証（ADR-0021 決定 1） ----

    public const string ForwarderMsiUploadSignInRequiredTitle =
        "MSI のアップロード・削除の操作には、サインインが必要です。";

    /// <summary>「なぜこの操作だけ認証が要るか」の平易な説明（ADR-0021 決定 2）。</summary>
    public const string ForwarderMsiUploadSignInRequiredExplanation =
        "この画面の他の機能はサーバ上のブラウザからそのまま使えますが、ここに配置した MSI は"
        + "全端末に配布されるため、アップロード・削除だけは「誰が行ったか」を記録できるよう、"
        + "サインインした管理者に限定しています（故障ではありません）。";

    public const string ForwarderMsiUploadSignInLinkLabel = "サインイン画面を開く";

    // ---- opt-in の有効化・無効化トグル（ADR-0021 決定 4） ----

    public const string ForwarderMsiUploadEnableSectionTitle = "アップロード機能を有効にする";

    /// <summary>反映に再起動（= 受信断の窓 1 回）が要ることを隠さない（ADR-0021 決定 4）。</summary>
    public const string ForwarderMsiUploadEnableRestartNotice =
        "この設定の反映にはサービスの再起動が必要です（再起動の間は syslog を受信できません）。"
        + "有効化しても、配置フォルダへの書き込み権限を付与するまでアップロードは実行できません。";

    public const string ForwarderMsiUploadEnableSignInRequired =
        "この設定の変更にはサインインが必要です（MSI の書き込み口を出現させる操作のため、"
        + "アップロード操作と同じ扱いです）。";

    /// <summary>切替時点検（ADR-0021 決定 1 の事前仕込み対処）。</summary>
    public const string ForwarderMsiUploadEnableInventoryTitle =
        "有効化の前に、既存のアプリ独自認証アカウントを確認してください:";

    /// <summary>{0} = ユーザー名、{1} = 作成時刻、{2} = 最終変更時刻、{3} = 最終ログイン。</summary>
    public const string ForwarderMsiUploadEnableInventoryDetailFormat =
        "ユーザー名: {0} / 作成: {1} / 最終変更: {2} / 最終ログイン: {3}";

    public const string ForwarderMsiUploadEnableInventoryTimestampUnknown = "記録なし";

    /// <summary>
    /// アップグレード環境で作成・変更時刻が欠ける場合の理由説明。「壊れている」と
    /// 読まれないよう理由を明示し、心当たりのないアカウントの扱いまで誘導する。
    /// </summary>
    public const string ForwarderMsiUploadEnableInventoryLegacyNotice =
        "作成・最終変更の時刻は、この機能を導入したバージョンより前に作られたアカウントでは"
        + "記録されていません（時刻を後から復元することはできないため「記録なし」と表示しています）。"
        + "その場合は最終ログインを手がかりに、心当たりのないアカウントであれば有効化の前に"
        + "パスワードの再設定または削除を検討してください。";

    public const string ForwarderMsiUploadEnableInventoryAcknowledge =
        "このアカウントが自分（または信頼できる管理者）が作成したものであることを確認しました";

    public const string ForwarderMsiUploadEnableButton = "アップロード機能を有効にする";

    public const string ForwarderMsiUploadDisableButton = "アップロード機能を無効にする";

    public const string ForwarderMsiUploadEnabledSaved =
        "アップロード機能を有効にしました。反映にはサービスの再起動が必要です。";

    public const string ForwarderMsiUploadDisabledSaved =
        "アップロード機能を無効にしました。反映にはサービスの再起動が必要です。";

    public const string ForwarderMsiUploadSettingNoChange = "設定は変更されていません。";

    public const string ForwarderMsiUploadDisabledManualGuide =
        "この構成のままでも、サーバのファイルシステムへの手動配置（利用者ガイド参照）で MSI 同梱を利用できます。";

    /// <summary>書き込み経路が未開放（ACE 未付与）のときの案内（ADR-0020 決定 2——ここも沈黙にしない）。</summary>
    public const string ForwarderMsiUploadNotWritableTitle =
        "アップロード機能は有効ですが、書き込み経路がまだ開放されていません。";

    public const string ForwarderMsiUploadNotWritableIntro =
        "Yagura は自分では配置フォルダの権限を変更しません。OS の管理者が次のコマンドで、サービス実行"
        + "アカウントに配置フォルダ限定の書き込み権限を付与すると、アップロード（および削除）が使えるように"
        + "なります（撤去も自由です。手順の詳細・撤去コマンド・OS 監査（SACL）の推奨設定は利用者ガイド参照）:";

    /// <summary>{0} = 配置フォルダのフルパス、{1} = サービス実行アカウント名。</summary>
    public const string ForwarderMsiUploadGrantCommandFormat =
        "icacls \"{0}\" /grant \"{1}:(OI)(CI)(M)\"";

    /// <summary>開放状態の常時表示（ADR-0020 決定 2——閉じ忘れの検出可能性を画面でも支える）。</summary>
    public const string ForwarderMsiUploadWritePathOpenNotice =
        "書き込み経路が開放されています（サービス実行アカウントが配置フォルダへ書き込めます）。"
        + "「使うときだけ開く」運用の場合は、作業後に権限の撤去を忘れないでください。";

    public const string ForwarderMsiUploadFileLabel = "アップロードする MSI ファイル";

    public const string ForwarderMsiUploadButton = "アップロードして内容を確認";

    /// <summary>{0} = 上限（MiB）。</summary>
    public const string ForwarderMsiUploadFileTooLargeFormat =
        "選択されたファイルはサイズ上限（{0} MiB）を超えています。送信は行いませんでした。";

    public const string ForwarderMsiUploadNoFileSelected = "ファイルが選択されていません。";

    public const string ForwarderMsiUploadInProgress = "アップロード中です…";

    // ---- 確認（stage → commit の二段階。ADR-0020 決定 3） ----

    public const string ForwarderMsiUploadConfirmTitle = "配置内容の確認";

    public const string ForwarderMsiUploadConfirmIntro =
        "まだ配置は確定していません。内容を確認し、「配置を確定」を押すと配布キット生成の対象になります。";

    /// <summary>{0} = 格納ファイル名（ProductVersion から Yagura が生成）。</summary>
    public const string ForwarderMsiUploadConfirmFileNameFormat = "格納ファイル名: {0}";

    public const string ForwarderMsiUploadReplaceWarningTitle =
        "同じアーキテクチャの MSI が既に配置されています。確定すると置き換えられます。";

    /// <summary>{0} = 既存（置換される側）の SHA256。</summary>
    public const string ForwarderMsiUploadReplaceOldSha256Format = "現在の SHA256（置換される側）: {0}";

    /// <summary>{0} = 新しくアップロードした側の SHA256。</summary>
    public const string ForwarderMsiUploadReplaceNewSha256Format = "新しい SHA256（アップロードした側）: {0}";

    public const string ForwarderMsiUploadReplaceAcknowledge = "既存の MSI を置き換えることを確認しました";

    /// <summary>公式ハッシュ不一致（未知の版を含む）の二段階確認（ADR-0020 決定 3）。</summary>
    public const string ForwarderMsiUploadHashMismatchWarning =
        "このファイルは、Yagura が検証済みとして把握している公式配布の SHA256 と一致しません。"
        + "改ざんされたファイル・誤ったバージョン・未検証のビルドの可能性があります。"
        + "自分が信頼できる入手元（公式配布ページ）からダウンロードしたことを確認したうえで進めてください。";

    public const string ForwarderMsiUploadHashMismatchAcknowledge =
        "公式配布の SHA256 と一致しないことを理解したうえで配置します";

    public const string ForwarderMsiUploadCommitButton = "配置を確定";

    public const string ForwarderMsiUploadCancelButton = "中止（アップロードを破棄）";

    public const string ForwarderMsiUploadCommitted = "MSI を配置しました。上の検出結果に反映されています。";

    // ---- 削除（ADR-0020 決定 3——常に二段階確認） ----

    public const string ForwarderMsiDeleteButton = "配置済み MSI を削除";

    public const string ForwarderMsiDeleteConfirmTitle = "配置済み MSI の削除の確認";

    /// <summary>{0} = ファイル名、{1} = SHA256。</summary>
    public const string ForwarderMsiDeleteConfirmDetailFormat =
        "削除対象: {0}（SHA256: {1}）。削除すると、この MSI は配布キット生成の同梱対象から外れます。";

    public const string ForwarderMsiDeleteAcknowledge = "この MSI を削除することを確認しました";

    public const string ForwarderMsiDeleteConfirmButton = "削除を確定";

    public const string ForwarderMsiDeleteCancelButton = "やめる";

    public const string ForwarderMsiDeleted = "配置済み MSI を削除しました。";

    // ---- エラー（アップロード/削除の失敗。理由は監査 3014 に構造化して残る） ----

    public const string ForwarderMsiUploadErrorGeneric =
        "アップロードに失敗しました。詳細は監査記録・イベントログを確認してください。";

    public const string ForwarderMsiUploadErrorBusy =
        "別のアップロードが進行中です。完了を待ってからやり直してください。";

    public const string ForwarderMsiUploadErrorTooLarge = "ファイルがサイズ上限を超えています。";

    public const string ForwarderMsiUploadErrorDiskSpace =
        "配置先ボリュームの空き容量が不足しています（受信用の空きを確保するため受け付けませんでした）。";

    public const string ForwarderMsiUploadErrorWriteFailed =
        "書き込みに失敗しました。書き込み経路（権限の付与状態）を確認してください。";

    public const string ForwarderMsiUploadErrorVersionUnreadable =
        "MSI から版（ProductVersion）を読み取れませんでした。ファイルが MSI 形式であること・破損していないことを確認してください。";

    public const string ForwarderMsiUploadErrorMultipleExisting =
        "配置フォルダに同じアーキテクチャの MSI が複数あります。先に手動で 1 つに整理してください。";

    public const string ForwarderMsiUploadErrorStateChanged =
        "確認を表示してから配置フォルダの状態が変わりました。安全のため確定を中止しました。最初からやり直してください。";

    public const string ForwarderMsiUploadErrorNotAcknowledged = "確認チェックが入っていません。";

    public const string ForwarderMsiDeleteErrorMismatch =
        "確認を表示してからファイルが変わりました。安全のため削除を中止しました。画面を更新してやり直してください。";

    /// <summary>専用認可の拒否（ADR-0021 決定 1。未サインイン・サインイン切れ）。</summary>
    public const string ForwarderMsiUploadErrorAuthenticationRequired =
        "この操作にはサインインが必要です（サインインの有効期限が切れた場合を含みます）。"
        + "サインインし直してから、最初からやり直してください。";
}
