namespace Yagura.Web.Components.Common;

/// <summary>
/// 旧・組み込み DB ファイルの処分（database.md §6.3）の文言。画面上の呼び名は「後始末」、退避は
/// 「書き出して保管」とし、「退避」の語を使わない（一時保管への退避と取り違えないため。ui.md §7.2）。
/// 中身を「切り替え前のログ」と決めつけず「以前の保存先（組み込みデータベース）のファイル」と呼ぶ。
/// </summary>
public static partial class UiText
{
    // ---- /admin の常設カード（ui.md §4・§5.4） ----

    /// <summary>
    /// 常設カードの見出し。切替前の予告（<see cref="PromotionSwitchWarning"/>）が「管理画面に『…』と表示されます」と
    /// 名指しする名前であり、両者を同じ文字列に保つ。
    /// </summary>
    public const string AdminOldDatabaseDisposalCardTitle = "後始末が残っています";

    /// <summary>
    /// 常設カードの本文。冒頭で受信と保存が正常に続いていることを示す（未完了は障害ではない）。カードは旧ファイルを
    /// 開かずに判定するため、中身を決めつけない呼び方に留める。
    /// </summary>
    public const string AdminOldDatabaseDisposalCardDescription =
        "受信と保存は正常に続いています。以前の保存先（組み込みデータベース）のファイルが残っています。" +
        "ログを保存しておく期間による自動削除は、このファイルには効きません。";

    /// <summary>常設カードから昇格ページの後始末セクションへの導線（状態を変える操作はカードに置かない）。</summary>
    public const string AdminOldDatabaseDisposalCardLink = "後始末する";

    /// <summary>常設カードの対象ファイルの要約（{0} = 件数・{1} = 合計サイズ）。</summary>
    public const string AdminOldDatabaseDisposalCardFilesFormat = "対象のファイル: {0} 件（合計 {1}）";

    /// <summary>常設カード・後始末セクション: 移行の実行中。</summary>
    public const string OldDatabaseDisposalMigrationRunning =
        "蓄積ログの移行を実行中です。移行が終わってから後始末できます。";

    /// <summary>常設カード: 後始末の実行中。</summary>
    public const string AdminOldDatabaseDisposalCardRunning = "後始末を実行中です。";

    /// <summary>常設カード: 直前の後始末が完了しなかった（{0} = 結果の説明）。</summary>
    public const string AdminOldDatabaseDisposalCardLastFailedFormat = "直前の後始末は完了しませんでした（{0}）";

    /// <summary>
    /// 常設カード・後始末セクション: 切り替えの時点に記録した旧ファイルが別の名前で残っている（{0} = パス）。
    /// 製品は今の設定から導いたファイルしか後始末しないため、画面の操作では解消できない。
    /// </summary>
    public const string OldDatabaseDisposalMisplacedFormat =
        "切り替えの時点の組み込みデータベースのファイル（{0}）が、現在の設定（Storage:SqliteFileName）とは別の名前で" +
        "残っています。管理画面の操作では後始末できません。利用者向けドキュメントの手作業の手順で後始末してください。";

    // ---- 昇格ページの後始末セクション（ui.md §5.4） ----

    /// <summary>後始末セクションの見出し。</summary>
    public const string OldDatabaseDisposalSectionTitle = "以前の保存先（組み込みデータベース）のファイルの後始末";

    /// <summary>後始末セクションの説明（なぜ必須か・元に戻せないこと）。</summary>
    public const string OldDatabaseDisposalSectionDescription =
        "受信と保存は正常に続いています。以前の保存先（組み込みデータベース）のファイルが残っています。" +
        "ログを保存しておく期間による自動削除は、このファイルには効かないため、後始末（必須）で削除するか、" +
        "保管用に書き出してから削除します。後始末の後は元に戻せません。";

    /// <summary>対象ファイルの一覧の見出し。</summary>
    public const string OldDatabaseDisposalTargetFilesLabel = "対象のファイル";

    /// <summary>対象ファイル 1 件（{0} = パス・{1} = サイズ）。</summary>
    public const string OldDatabaseDisposalTargetFileFormat = "{0}（{1}）";

    /// <summary>サイズを読めなかったファイルの表示。</summary>
    public const string OldDatabaseDisposalSizeUnknown = "サイズ不明";

    /// <summary>対象ファイルの合計（{0} = 合計サイズ）。</summary>
    public const string OldDatabaseDisposalTotalSizeFormat = "合計 {0}";

    /// <summary>移行の状態の見出し。</summary>
    public const string OldDatabaseDisposalMigrationStateLabel = "蓄積ログの移行";

    /// <summary>
    /// 移行の状態: 完了（完了記録と旧ファイルの件数による判定）。時間帯ごとの照合は後始末の直前に行う
    /// （database.md §6.3「状態表示では処分の前に照合すると示す」）。
    /// </summary>
    public const string OldDatabaseDisposalMigrationCompleted =
        "完了しています。後始末の前に、SQL Server との件数を時間帯ごとに照合します。";

    /// <summary>移行の状態: 未完了。</summary>
    public const string OldDatabaseDisposalMigrationNotCompleted =
        "完了していません。これまでのログを画面で見る場合は、先に『蓄積ログの移行』を実行してください" +
        "（移行しない場合は、後始末の確認で明示的に確認します）。";

    /// <summary>移行の状態: 移行するログが無い（本体が無い）。</summary>
    public const string OldDatabaseDisposalMigrationNoSource =
        "移行の対象（組み込みデータベースの本体）はありません。";

    /// <summary>照合の実行ボタン。</summary>
    public const string OldDatabaseDisposalInspect = "照合して後始末の内容を確かめる";

    /// <summary>照合の説明（全体を読むこと・その間は移行を開始できないこと）。</summary>
    public const string OldDatabaseDisposalInspectNote =
        "照合では、以前の保存先のファイル全体を読みます（ファイルが大きいと時間がかかります。その間は移行を開始できません）。";

    /// <summary>照合の実行中。</summary>
    public const string OldDatabaseDisposalInspecting = "以前の保存先のファイルを照合しています…";

    /// <summary>照合できなかった（移行・別の後始末・別の照合が旧ファイルを使用中）。</summary>
    public const string OldDatabaseDisposalInspectionUnavailable =
        "照合できませんでした（別の操作が以前の保存先のファイルを使用中です）。しばらくしてから、もう一度照合してください。";

    /// <summary>対象が安全検査を満たさない（{0} = 理由）。</summary>
    public const string OldDatabaseDisposalTargetUnsafeFormat =
        "以前の保存先のファイルが安全検査を満たさないため、この画面では後始末できません（{0}）。" +
        "利用者向けドキュメントの手作業の手順で後始末してください。";

    /// <summary>本体が 0 バイト。</summary>
    public const string OldDatabaseDisposalTargetEmptyBody =
        "組み込みデータベースのファイルは空（0 バイト）です。内容を持たないため、削除で後始末します。";

    /// <summary>本体が無く付随ファイルだけが残る。</summary>
    public const string OldDatabaseDisposalTargetSidecarFilesOnly =
        "組み込みデータベースの本体は無く、付随するファイル（-wal・-shm）だけが残っています。削除で後始末します。";

    /// <summary>本体の先頭の識別子が無い・壊れている。</summary>
    public const string OldDatabaseDisposalTargetUnverifiable =
        "組み込みデータベースのファイルとして確認できない（先頭の識別子が無い・壊れている）ため、内容を確かめられません。" +
        "削除でのみ後始末できます。";

    /// <summary>旧ファイルのログ件数（{0} = 件数・{1} = 期間）。</summary>
    public const string OldDatabaseDisposalRecordCountFormat = "ログ {0:N0} 件（{1}）";

    /// <summary>旧ファイルのログ件数（0 件）。</summary>
    public const string OldDatabaseDisposalRecordCountZero = "ログ 0 件";

    /// <summary>旧ファイルのシステムイベント件数（{0} = 件数）。用語対応表: システムイベント → 受信できなかった時間帯などの記録。</summary>
    public const string OldDatabaseDisposalSystemEventCountFormat = "受信できなかった時間帯などの記録 {0:N0} 件";

    /// <summary>期間の表示（{0} = 最古・{1} = 最新）。</summary>
    public const string OldDatabaseDisposalRangeFormat = "{0}〜{1}";

    /// <summary>照合の結果: 移行済み。</summary>
    public const string OldDatabaseDisposalMigrated =
        "移行済みです（時間帯ごとの照合に合格しました）。ログは SQL Server に移っています。";

    /// <summary>
    /// 移行の完了後に追加されたログ・照合できない区間のログ（{0} = 件数・{1} = 期間。ui.md §5.4）。
    /// 縮退運転で書き込まれたログは他のどこにも無いため、「古い残骸」と読ませない。
    /// </summary>
    public const string OldDatabaseDisposalRecordsAfterSwitchFormat =
        "SQL Server に保存できなかった間に、組み込みデータベースへ保存したログが {0:N0} 件あります（{1}）。" +
        "これらは SQL Server にはありません。先に『蓄積ログの移行』を実行してください。";

    /// <summary>移行の完了後に追加されたログ（件数を数えられない場合——切り替えの時点の記録が無い）。</summary>
    public const string OldDatabaseDisposalRecordsAddedAfterCompletion =
        "SQL Server に保存できなかった間に、組み込みデータベースへ保存したログがあります。" +
        "これらは SQL Server にはありません。先に『蓄積ログの移行』を実行してください。";

    /// <summary>未移行のログ（{0} = 件数・{1} = 期間）。</summary>
    public const string OldDatabaseDisposalUnmigratedFormat = "まだ移行していないログが {0:N0} 件あります（{1}）。";

    /// <summary>移行先に無いシステムイベント（{0} = 件数・{1} = 期間）。</summary>
    public const string OldDatabaseDisposalUnmigratedSystemEventsFormat =
        "SQL Server に無い『受信できなかった時間帯』などの記録が {0:N0} 件あります（{1}）。蓄積ログの移行で引き継げます。";

    /// <summary>照合の不合格: 時間帯ごとの照合で移行先が少ない時間帯がある。</summary>
    public const string OldDatabaseDisposalGapHourly =
        "時間帯ごとの照合で、SQL Server の件数が組み込みデータベースより少ない時間帯があります。";

    /// <summary>照合の制約: 切り替えの時点の記録が無い（照合できない区間を全区間とみなす）。</summary>
    public const string OldDatabaseDisposalGapBoundaryUnknown =
        "切り替えの時点の記録が無いため、どこからが SQL Server に保存できなかった間のログかを判別できません" +
        "（すべての期間を照合できないものとして扱います）。";

    /// <summary>照合の制約: 移行先が時間帯ごとの照合に対応していない。</summary>
    public const string OldDatabaseDisposalGapTargetCannotReconcile =
        "現在の保存先は時間帯ごとの照合に対応していないため、照合できません。";

    /// <summary>後始末の方法の選択のラベル。</summary>
    public const string OldDatabaseDisposalMethodLabel = "後始末の方法";

    /// <summary>後始末の方法の空の選択肢（既定の選択は置かない）。</summary>
    public const string OldDatabaseDisposalMethodEmptyOption = "（選んでください）";

    /// <summary>方式: 書き出して保管。</summary>
    public const string OldDatabaseDisposalMethodArchive = "書き出して保管";

    /// <summary>方式: 削除。</summary>
    public const string OldDatabaseDisposalMethodDelete = "削除";

    /// <summary>書き出して保管の説明（ui.md §5.4）。</summary>
    public const string OldDatabaseDisposalMethodArchiveDescription =
        "書き出して保管: 保管用のファイルに書き出してから、元のファイルを削除します。書き出したファイルは、" +
        "以後この製品では管理せず、この製品の画面で開くこともできません（保管・バックアップ・不要になったときの" +
        "削除は管理者が行ってください）。";

    /// <summary>削除の説明（ui.md §5.4）。</summary>
    public const string OldDatabaseDisposalMethodDeleteDescription = "削除: 元のファイルを削除します。元に戻せません。";

    /// <summary>
    /// 選択の目安（ui.md §5.4）。切替前のシステムイベントが移行で引き継がれること（DB-13）を前提にする。
    /// </summary>
    public const string OldDatabaseDisposalMethodGuidance =
        "移行が完了していれば、通常は『削除』で足ります（ログは SQL Server に移っています。削除の前に、" +
        "SQL Server のバックアップが取られていることを確認してください）。元のファイルそのものの保管を" +
        "求められている場合だけ、『書き出して保管』を選んでください。";

    /// <summary>この対象では書き出して保管を選べない。</summary>
    public const string OldDatabaseDisposalArchiveNotAllowed =
        "このファイルは内容を確かめられないため、書き出して保管は選べません。";

    /// <summary>保存期間を過ぎたログも書き出したファイルに含まれる（{0} = 保存期間の日数・{1} = 件数）。</summary>
    public const string OldDatabaseDisposalBeyondRetentionFormat =
        "ログを保存しておく期間（{0} 日）を過ぎたログ {1:N0} 件も、書き出したファイルに含まれます。";

    /// <summary>保管先の入力ラベル（入力するのはサーバ上のパス——ブラウザの手元の PC ではない）。</summary>
    public const string OldDatabaseDisposalDestinationLabel = "保管先のフォルダ（このサーバ上のローカルフォルダ）";

    /// <summary>保管先の条件（入力の前に示す。{0} = 必要な空き容量）。</summary>
    public const string OldDatabaseDisposalDestinationConditionsFormat =
        "このサーバのローカルドライブにある、既存のフォルダを指定します（この中に保管用のフォルダを作ります）。" +
        "ネットワーク共有・USB メモリ等の取り外せる媒体・Yagura のデータフォルダの中は使えません。必要な空き容量: 約 {0}";

    /// <summary>保管先の入力例。</summary>
    public const string OldDatabaseDisposalDestinationPlaceholder = @"例: D:\Archive";

    /// <summary>保管先の事前検査ボタン。</summary>
    public const string OldDatabaseDisposalDestinationCheck = "保管先を確かめる";

    /// <summary>保管先にフォルダを作れないときの付与コマンドのラベル。</summary>
    public const string OldDatabaseDisposalGrantCommandLabel = "フォルダの作成を許すコマンド（管理者として実行）";

    /// <summary>後始末の後に付与を外すコマンドのラベル。</summary>
    public const string OldDatabaseDisposalRevokeCommandLabel = "後始末の後に、付与を外すコマンド";

    /// <summary>
    /// 低トラフィック帯での実行の推奨。所要時間の目安（DB-12 ⑥の実測）が確定するまでは、大きさに比例する
    /// ことだけを示す。
    /// </summary>
    public const string OldDatabaseDisposalLowTrafficNote =
        "受信の少ない時間帯での実行をおすすめします（後始末にかかる時間は、ファイルの大きさにほぼ比例します）。" +
        "実行中は画面を閉じても処理は続きます。";

    /// <summary>後始末の実行ボタン（破壊的操作。確認ダイアログ必須——ui.md §3.1）。</summary>
    public const string OldDatabaseDisposalExecute = "後始末を実行する";

    /// <summary>確認ダイアログの見出し。</summary>
    public const string OldDatabaseDisposalConfirmTitle = "以前の保存先のファイルを後始末します";

    /// <summary>確認ダイアログの確認ボタン。</summary>
    public const string OldDatabaseDisposalConfirmAction = "後始末を実行する";

    /// <summary>確認ダイアログの最上段（削除）。</summary>
    public const string OldDatabaseDisposalConfirmSummaryDelete = "元に戻せません。元のファイルを削除します。";

    /// <summary>確認ダイアログの最上段（書き出して保管）。</summary>
    public const string OldDatabaseDisposalConfirmSummaryArchive =
        "元に戻せません。保管用のファイルに書き出してから、元のファイルを削除します。";

    /// <summary>未移行のログ（削除。{0} = 件数・{1} = 期間。ui.md §5.4）。</summary>
    public const string OldDatabaseDisposalConfirmUnmigratedDeleteFormat =
        "まだ移行していないログが {0:N0} 件あります（{1}）。削除すると、これらのログは画面から参照できないまま失われます。";

    /// <summary>未移行のログ（書き出して保管。{0} = 件数・{1} = 期間。ui.md §5.4）。</summary>
    public const string OldDatabaseDisposalConfirmUnmigratedArchiveFormat =
        "まだ移行していないログが {0:N0} 件あります（{1}）。書き出したファイルには残りますが、この製品の画面からは参照できません。";

    /// <summary>移行先に無いシステムイベント（削除。{0} = 件数・{1} = 期間）。</summary>
    public const string OldDatabaseDisposalConfirmSystemEventsDeleteFormat =
        "SQL Server に無い『受信できなかった時間帯』などの記録が {0:N0} 件あります（{1}）。削除すると失われます。";

    /// <summary>移行先に無いシステムイベント（書き出して保管。{0} = 件数・{1} = 期間）。</summary>
    public const string OldDatabaseDisposalConfirmSystemEventsArchiveFormat =
        "SQL Server に無い『受信できなかった時間帯』などの記録が {0:N0} 件あります（{1}）。" +
        "書き出したファイルには残りますが、この製品の画面からは参照できません。";

    /// <summary>「移行せずに処分する」の明示確認（既定は未確認）。</summary>
    public const string OldDatabaseDisposalConfirmWithoutMigration = "移行せずに後始末する";

    /// <summary>内容を確かめられない本体の削除（{0} = ファイル名・{1} = サイズ）。</summary>
    public const string OldDatabaseDisposalConfirmUnverifiableFormat =
        "組み込みデータベースのファイルとして確認できないファイルです（{0}、{1}）。内容を確かめられないまま削除します。";

    /// <summary>内容を確かめられない本体の削除の明示確認。</summary>
    public const string OldDatabaseDisposalConfirmUnverifiableLabel = "このファイルを削除してよいことを確認しました";

    /// <summary>確認ダイアログの補足: 方式。</summary>
    public const string OldDatabaseDisposalConfirmMethodLabel = "後始末の方法";

    /// <summary>確認ダイアログの補足: 対象ファイル。</summary>
    public const string OldDatabaseDisposalConfirmTargetsLabel = "対象のファイル";

    /// <summary>確認ダイアログの補足: 保管先。</summary>
    public const string OldDatabaseDisposalConfirmDestinationLabel = "保管先";

    /// <summary>確認ダイアログの補足: 保管ファイル名。</summary>
    public const string OldDatabaseDisposalConfirmArchiveFileLabel = "保管ファイル名";

    /// <summary>
    /// 保管ファイル名の規則（{0} = 接頭辞・{1} = 拡張子）。実際の日時は実行時に決まり、結果に示す。
    /// </summary>
    public const string OldDatabaseDisposalConfirmArchiveFileFormat =
        "{0}（実行時の日時〔UTC〕）{1}（保管先の中に同じ名前の保管用フォルダを作り、その中に書き出します）";

    /// <summary>確認ダイアログの補足: 保存期間を過ぎたログ（書き出したファイルに含まれる）。</summary>
    public const string OldDatabaseDisposalConfirmBeyondRetentionLabel = "ログを保存しておく期間を過ぎたログ";

    /// <summary>確認ダイアログの補足: 保護責任の移転。</summary>
    public const string OldDatabaseDisposalConfirmResponsibilityLabel = "書き出したファイルの管理";

    /// <summary>保護責任の移転（書き出したファイルは製品の管理外になる）。</summary>
    public const string OldDatabaseDisposalResponsibility =
        "書き出したファイルは、以後この製品では管理せず、この製品の画面で開くこともできません。" +
        "保管・バックアップ・不要になったときの削除は管理者が行ってください。";

    /// <summary>確認ダイアログの補足: 削除の前の確認。</summary>
    public const string OldDatabaseDisposalConfirmBackupLabel = "削除の前に";

    /// <summary>削除の後は移行先が唯一のコピーになる。</summary>
    public const string OldDatabaseDisposalConfirmBackup = "SQL Server のバックアップが取られていることを確認してください。";

    /// <summary>実行中（{0} = 方式・{1} = 開始時刻）。</summary>
    public const string OldDatabaseDisposalRunningFormat =
        "後始末を実行中です（方法: {0}。開始: {1}）。画面を閉じても処理は続きます。";

    /// <summary>段階: WAL の反映。</summary>
    public const string OldDatabaseDisposalStageCheckpointing = "以前の保存先のファイルの内容をまとめています。";

    /// <summary>段階: 書き出し。</summary>
    public const string OldDatabaseDisposalStageExporting = "保管用のファイルに書き出しています。";

    /// <summary>段階: 残留の除去。</summary>
    public const string OldDatabaseDisposalStageRemovingResidue =
        "書き出したファイルから、管理者アカウントの情報と削除済みのデータを除いています。";

    /// <summary>段階: 検証。</summary>
    public const string OldDatabaseDisposalStageVerifying = "書き出したファイルを検証しています。";

    /// <summary>段階: 確定。</summary>
    public const string OldDatabaseDisposalStageFinalizing = "書き出したファイルを確定しています（名前とアクセス権）。";

    /// <summary>段階: 削除（取り消せない）。</summary>
    public const string OldDatabaseDisposalStageDeleting = "元のファイルを削除しています。この段階は取り消せません。";

    /// <summary>取り消しボタン（削除の段階の前だけ）。</summary>
    public const string OldDatabaseDisposalCancel = "後始末を取り消す";

    /// <summary>取り消しを受け付けた。</summary>
    public const string OldDatabaseDisposalCancelAccepted = "取り消しを受け付けました。元のファイルは変更しません。";

    /// <summary>取り消せなかった（削除の段階に入った・既に終わった）。</summary>
    public const string OldDatabaseDisposalCancelRejected =
        "取り消せませんでした（元のファイルの削除を始めたか、後始末が既に終わっています）。";

    /// <summary>直前の後始末が完了しなかった（後始末セクションの見出し）。</summary>
    public const string OldDatabaseDisposalLastFailedTitle = "直前の後始末は完了しませんでした";

    /// <summary>後始末の完了（後始末セクションの見出し）。</summary>
    public const string OldDatabaseDisposalCompletedTitle = "後始末が完了しました";

    /// <summary>結果: 書き出したファイル。</summary>
    public const string OldDatabaseDisposalResultArchiveFileLabel = "書き出したファイル";

    /// <summary>結果: 書き出したファイルの SHA-256（監査記録にも残る——提出されたファイルの照合に使う）。</summary>
    public const string OldDatabaseDisposalResultSha256Label = "書き出したファイルの SHA-256（監査記録にも残しています）";

    /// <summary>結果: 削除できずに残ったファイル。</summary>
    public const string OldDatabaseDisposalResultRemainingFilesLabel = "削除できずに残ったファイル";

    /// <summary>結果: 作業用のフォルダを削除できなかった（{0} = パス）。</summary>
    public const string OldDatabaseDisposalResultWorkDirectoryLeftFormat =
        "作業用のフォルダを削除できませんでした（{0}）。管理者だけが読めるアクセス権のまま残っています。" +
        "管理者として中身を確かめ、削除してください。";

    /// <summary>後始末の開始に失敗した・拒否された（見出し）。</summary>
    public const string OldDatabaseDisposalRejectedTitle = "後始末を開始できませんでした";
}
