namespace Yagura.Storage;

/// <summary>
/// ログ永続化 provider の抽象（database.md §1.2 provider 契約）。
/// </summary>
/// <remarks>
/// <para>
/// M5 時点で database.md §1.2 の契約 6 項目（スキーマ管理・バッチ挿入・失敗の分類報告・
/// 対話的検索・保持期間の削除・統計）を完全化した（M5-1。#45）。契約への操作追加は
/// database.md §7 の「optional 操作（capability 検出）として MINOR で追加できる」互換規則に
/// 従う——既存操作の意味論は変更しない。
/// </para>
/// <para>
/// <b>書き込みは単一 writer が呼び出す契約とする</b>（database.md §4）。
/// <see cref="WriteBatchAsync"/>・<see cref="WriteSystemEventAsync"/>・
/// <see cref="DeleteOlderThanAsync"/> の呼び出しは、呼び出し側が直列化する責務を持ち、
/// 本インターフェースの<b>実装（provider）自体は複数呼び出し元からの並行呼び出しに対する
/// 排他制御を提供しない</b>——排他は provider の実装詳細ではなく呼び出し側の責務のまま
/// 据え置く。
/// </para>
/// <para>
/// <b>実配線（Issue #151。M5-1 完了後に判明した契約と実配線の乖離への対応）</b>: 実際には
/// (a) 永続化段（ライブ書き込み）・(b) スプール drain・(c) 保持期間削除（+ 実行記録の
/// システムイベント）の 3 経路が、独立したタスクから並行して本インターフェースの書き込み系
/// メソッドを呼び出し得る。ホスト（<c>Yagura.Host.Program</c>）はこれを「呼び出し側の直列化」
/// として実現するため、単一の <see cref="LogStoreWriteGate"/> インスタンスを構築し、3 経路
/// すべて（<c>PersistenceWriter</c>・<c>SpoolDrainCoordinator</c>・<c>RetentionScheduler</c>）へ
/// 同じインスタンスを渡す。<b>本インターフェースの契約自体（provider は排他を提供しない）は
/// 変えていない</b>——変わったのは「呼び出し側が直列化する」という既存契約を実際に満たす
/// 呼び出し配線が加わったことである。詳細な設計判断は <see cref="LogStoreWriteGate"/> の
/// doc コメントを参照。
/// </para>
/// <para>
/// <b>ゲートを通らない第 4 の書き込み経路（起動順序による非同時実行）</b>: 上記 3 経路の
/// ほかに、ホストの起動処理（<c>Yagura.Host.IngestionHostedService.StartAsync</c>）が受信断
/// 区間のシステムイベントを <see cref="WriteSystemEventAsync"/> で直接 1 回書き込む。この
/// 呼び出しはゲートを通らないが、消費ループ（永続化段・drain）と保持期間スケジューラの
/// 開始より厳密に前——起動シーケンス上、他の書き込み経路がまだ 1 つも動いていない時点——で
/// 実行されるため、非同時実行は起動順序により保証される（ゲートによる保証ではない。
/// 起動順序をリファクタする場合はこの前提が崩れないか確認すること——PR #198 レビュー指摘）。
/// </para>
/// <para>
/// <b>読み書き分離の性質の文書化義務</b>（database.md §1.2 契約表 末尾・§1.3）: 各 provider
/// 実装は、検索（読み取り）が書き込みをブロックし得るかと、付随する運用特性（WAL 肥大等）を
/// 実装クラスの doc コメントで明示すること。これは文書化義務であり、機械検証（適合テスト
/// スイート）の対象ではない——レビューで担保する。
/// </para>
/// </remarks>
public interface ILogStore
{
    /// <summary>
    /// スキーマを新規作成し、必要なら版間移行を適用する。<b>冪等</b>——既に現行バージョンへ
    /// 適用済みなら何もしない（database.md §1.2 契約 1）。
    /// </summary>
    /// <exception cref="SchemaPermissionException">
    /// スキーマ管理に必要な権限が不足している場合。不足内容と管理者が実行できる SQL を
    /// 例外のプロパティから区別可能に取得できる（database.md §5.2。SQL Server provider で
    /// 実体化する。SQLite provider は権限不足を <see cref="LogStoreWriteException"/>
    /// （<see cref="LogStoreFailureKind.Permanent"/>）として報告する——doc コメント参照）。
    /// </exception>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// ログレコードをバッチ書き込みする（database.md §1.2 契約 2、architecture.md §7 要求①）。
    /// </summary>
    /// <param name="records">書き込むレコードの集合。<see cref="LogRecord.Id"/> は無視され、provider が採番する。</param>
    /// <exception cref="LogStoreWriteException">
    /// 書き込みに失敗した場合。<see cref="LogStoreWriteException.FailureKind"/> で
    /// 一時障害・恒久障害・容量枯渇を区別する（database.md §1.2 契約 3）。
    /// </exception>
    Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken = default);

    /// <summary>
    /// 最新 <paramref name="limit"/> 件を <see cref="LogRecord.ReceivedAt"/> 降順で返す
    /// （database.md §1.2 契約 4 の特殊形。条件なし・射影は既定の先頭 N 文字の
    /// <see cref="LogQuery"/> として <see cref="QueryAsync"/> に委譲する）。
    /// </summary>
    /// <param name="limit">返却件数の上限。</param>
    /// <param name="timeout">クエリの実行時間上限。超過時は例外を送出する。</param>
    Task<IReadOnlyList<LogRecordSummary>> QueryLatestAsync(
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 条件付きの対話的検索（database.md §1.2 契約 4）。条件（時間範囲・送信元・重大度の閾値・
    /// facility・解析状態・自由文）+ 射影（一覧用の軽量列。先頭 N 文字）+ 結果上限 + タイムアウトを
    /// <see cref="LogQuery"/> で必須引数化する。同一 <see cref="LogRecord.ReceivedAt"/> の行は
    /// <see cref="LogRecord.Id"/> 降順でタイブレークする（Issue #144。結果順序の決定性）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// この防御（上限・タイムアウトの必須化）は UI の対話的検索が対象であり、
    /// database.md §1.2「契約拡張の予約」（一括読み出し・集計）の管理経路には適用しない。
    /// </para>
    /// <para>
    /// <b>カーソル（キーセット）ページング</b>（database.md §1.2・DB-11。Issue #144）:
    /// <see cref="LogQuery.Cursor"/> を指定すると、先頭ページと同じ条件のまま「続き」
    /// （カーソルより過去の行）だけを返す。実装は <c>ORDER BY ReceivedAt DESC, Id DESC</c> と
    /// 同じ複合キーに対するシーク（<c>WHERE (ReceivedAt, Id) &lt; (@cursorReceivedAt, @cursorId)</c>
    /// 相当）で行い、複合索引 <c>IX_LogRecords_ReceivedAt_Id</c> に乗る——OFFSET は使わない
    /// （ページが深くなっても性能が劣化しない）。詳細は <see cref="LogQueryCursor"/> 参照。
    /// </para>
    /// </remarks>
    /// <exception cref="TimeoutException">クエリが <see cref="LogQuery.Timeout"/> を超過した場合。</exception>
    Task<IReadOnlyList<LogRecordSummary>> QueryAsync(
        LogQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// システムイベント（database.md §2.3。受信断区間・保持期間削除の実行記録等）を 1 件書き込む
    /// （M4-4。architecture.md §4.4「受信断区間の保存…は通常のパイプライン（スプールを含む
    /// 耐障害経路）を通す——DB 障害中の起動でも記録が失われない」）。
    /// </summary>
    /// <param name="systemEvent">書き込むシステムイベント。<see cref="SystemEvent.Id"/> は無視され、provider が採番する。</param>
    /// <exception cref="LogStoreWriteException">書き込みに失敗した場合（database.md §1.2 契約 3 と同じ分類）。</exception>
    Task WriteSystemEventAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="cutoff"/> より古い（<see cref="LogRecord.ReceivedAt"/> &lt; cutoff）
    /// レコードを削除する（database.md §1.2 契約 5・§3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>分割実行</b>: 1 回の削除件数上限を設けて繰り返し実行し、受信の書き込みを長時間
    /// 妨げない（§3）。実行判断（スプール退避中・Q2 高水位・drain 進行中は開始しない等の
    /// 譲歩条件、容量枯渇契機の前倒し実行での譲歩例外）はホスト側スケジューラの責務であり、
    /// 本メソッド自体は「渡された cutoff まで分割削除を完遂する」ことのみを担う。
    /// </para>
    /// <para>
    /// <b>実行の記録</b>: 削除を実行したという事実（実行時刻・削除件数）は、呼び出し側が
    /// <see cref="WriteSystemEventAsync"/> で <c>Kind = "retention.delete"</c>
    /// （<see cref="RetentionConstants.SystemEventKindRetentionDelete"/>）のシステムイベントとして
    /// 記録する（database.md §2.3・§3。本メソッド自体はシステムイベントを書かない——
    /// 削除件数が呼び出し側に返るため、記録の責務は呼び出し側に置く）。
    /// </para>
    /// </remarks>
    /// <param name="cutoff">この時刻（UTC）より古いレコードを削除対象とする。</param>
    /// <exception cref="LogStoreWriteException">削除に失敗した場合（database.md §1.2 契約 3 と同じ分類）。</exception>
    Task<DeleteOlderThanResult> DeleteOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 統計情報（保存件数・DB サイズ）を取得する（database.md §1.2 契約 6）。
    /// </summary>
    Task<LogStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    // ------------------------------------------------------------------
    // M8-3（Issue #70）で追加した読み取り専用 3 操作。
    // database.md §1.2「契約拡張の予約」(a) 一括読み出し・(b) 集計のうち、閲覧 3 画面が
    // 必要とする最小の読み取り口を実体化した（詳細表示の個別取得・システムイベントの読み出し・
    // 送信元別集計）。v0.x のため直接メソッドとして追加する（v1.0 凍結時に optional 操作
    // ——capability 検出——へ移すかは §7 の凍結判断で確定する）。いずれも書き込みを行わない。
    // ------------------------------------------------------------------

    /// <summary>
    /// レコード識別子で 1 件を全項目（<see cref="LogRecord.Message"/> 全文・
    /// <see cref="LogRecord.StructuredData"/>・<see cref="LogRecord.Raw"/> を含む）取得する
    /// （architecture.md §6「全文は詳細表示で個別取得する」の読み取り口。M8-3）。
    /// </summary>
    /// <param name="id">レコード識別子（<see cref="LogRecordSummary.Id"/>）。</param>
    /// <param name="timeout">クエリの実行時間上限。</param>
    /// <returns>該当レコード。存在しない（保持期間削除等で消えた後を含む）場合は <c>null</c>。</returns>
    /// <exception cref="TimeoutException">クエリが <paramref name="timeout"/> を超過した場合。</exception>
    Task<LogRecord?> FindByIdAsync(
        long id,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// システムイベント（database.md §2.3。受信断区間・保持期間削除の実行記録等）を読み出す
    /// （受信断区間の時間軸表示・受信断履歴の表示の読み取り口——architecture.md §4.4
    /// 「受信断区間は検索・ダッシュボードで通常のログと同じ時間軸上に可視化する」。M8-3）。
    /// </summary>
    /// <param name="from">
    /// 区間の重なり判定の下限（UTC）。<c>null</c> は下限なし。指定時は
    /// 「<see cref="SystemEvent.EndAt"/> &gt;= from」で判定する（区間が範囲に少しでも掛かれば返す）。
    /// </param>
    /// <param name="to">
    /// 区間の重なり判定の上限（UTC）。<c>null</c> は上限なし。指定時は
    /// 「<see cref="SystemEvent.StartAt"/> &lt;= to」で判定する。
    /// </param>
    /// <param name="limit">結果件数の上限（必須。<see cref="SystemEvent.StartAt"/> 降順で新しい順に返す）。</param>
    /// <param name="timeout">クエリの実行時間上限。</param>
    /// <param name="kind">
    /// <see cref="SystemEvent.Kind"/> の完全一致フィルタ。<c>null</c> は全種別（従来互換）。
    /// Issue #150（保持期間削除の起動時キャッチアップ）で追加した最小の契約拡張——キャッチアップ
    /// 判定は直近の <c>retention.delete</c> イベントだけを必要とするが、種別を問わない直近 N 件の
    /// 走査では、<c>retention.delete</c> の <see cref="SystemEvent.StartAt"/> が意図的な過去日付
    /// （cutoff = 実行時刻 − 保持日数）である一方、受信断イベントの <see cref="SystemEvent.StartAt"/>
    /// は実時刻のため常に新しい側に並び、受信断が N 件を超えて蓄積すると削除記録がウィンドウから
    /// 恒常的に押し出される（PR #198 レビュー指摘）。種別で先に絞ることでこの非対称を解消する。
    /// </param>
    /// <exception cref="TimeoutException">クエリが <paramref name="timeout"/> を超過した場合。</exception>
    Task<IReadOnlyList<SystemEvent>> QuerySystemEventsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        TimeSpan timeout,
        string? kind = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 送信元別の受信状況（最終受信時刻・件数）を集計して返す（database.md §1.2
    /// 「契約拡張の予約」(b) 集計の実体化。UI-4 無音化検出の入力。M8-3）。
    /// </summary>
    /// <remarks>
    /// <b>並び順は <see cref="SourceActivity.LastReceivedAt"/> の昇順（古い順）</b>——
    /// 「いつも静かな装置が黙った」を検出するため、無音の疑いが強い送信元から返す
    /// （量の上位 N を返す集計にしない。ui.md §12 UI-4 の制約）。<paramref name="limit"/> は
    /// 送信元数の異常な膨張（送信元詐称等）に対する読み取り側の防御であり、打ち切りが起きた
    /// 場合も切り捨てられるのは「最近まで受信できている送信元」側である（昇順のため）。
    /// </remarks>
    /// <param name="limit">返却する送信元数の上限（必須）。</param>
    /// <param name="timeout">クエリの実行時間上限。</param>
    /// <exception cref="TimeoutException">クエリが <paramref name="timeout"/> を超過した場合。</exception>
    Task<IReadOnlyList<SourceActivity>> QuerySourceActivityAsync(
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 送信元別の受信状況を<b>最終受信時刻の降順（新しい順）</b>で集計して返す（Issue #383）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="QuerySourceActivityAsync"/>（昇順・無音化検出用）とは<b>打ち切りの向きが逆</b>
    /// である点が本メソッドの存在意義: 送信元数が <paramref name="limit"/> を超える環境で、
    /// 昇順版の結果を後から並べ替えても「最近まで受信している送信元」は既に切り捨てられている。
    /// 途絶検知のウォッチリスト候補選択（ADR-0018 決定 4——実在確認済みのアドレスから選ばせて
    /// 転記ミスを防ぐ）は「いま送ってきている送信元」を出すのが目的のため、DB 側で降順に
    /// 打ち切る本メソッドを使う。打ち切りで切り捨てられるのは「最後の受信が古い送信元」側。
    /// </para>
    /// <para>
    /// <b>既定実装</b>は <see cref="QuerySourceActivityAsync"/> へ委譲する——テスト用の偽実装や
    /// 送信元数が <paramref name="limit"/> 以下の環境では正しい結果になる（呼び出し側が降順へ
    /// 並べ替える）。実ストア（SQLite / SQL Server）は DB 側で <c>ORDER BY … DESC LIMIT</c> を
    /// 発行するよう本メソッドをオーバーライドし、打ち切りの向きを正す。
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<SourceActivity>> QueryMostRecentlyActiveSourcesAsync(
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        QuerySourceActivityAsync(limit, timeout, cancellationToken);

    // ------------------------------------------------------------------
    // M8-5（Issue #159）で追加した読み取り専用 2 操作。database.md §1.2「契約拡張の予約」
    // (b) 集計の追加実体化——重大度分布（平常時からの逸脱検知）と受信量上位の送信元
    // （Top talkers。フラッディング検知）。ダッシュボードに従来欠けていた 2 視点を満たす。
    // いずれも書き込みを行わない。
    // ------------------------------------------------------------------

    /// <summary>
    /// 観測窓内の重大度別件数を集計して返す（重大度分布。M8-5/Issue #159）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>窓の必須化と性能上の理由</b>: Severity 列には索引が無く（Issue #145 で追跡中の
    /// 既知の制約）、無条件の集計は大規模時にフルスキャンへ劣化し得る。本操作は
    /// <paramref name="from"/>・<paramref name="to"/> を必須引数とし、索引済みの
    /// <see cref="LogRecord.ReceivedAt"/> 範囲へ先に絞り込んでから集計することで、
    /// スキャン対象行数を観測窓の幅に限定する——複合索引の追加そのものは Issue #145 の
    /// 範囲であり、本操作はそれとは独立に「窓の必須化」で大規模時の劣化を抑える設計とする。
    /// </para>
    /// <para>
    /// PRI が解析できず <see cref="LogRecord.Severity"/> が未設定（null）のレコードも
    /// 1 バケットとして返す（解析失敗の事実を隠さない——§5.3 と同じ向き）。
    /// </para>
    /// </remarks>
    /// <param name="from">観測窓の下限（UTC、含む）。</param>
    /// <param name="to">観測窓の上限（UTC、含む）。</param>
    /// <param name="timeout">クエリの実行時間上限。</param>
    /// <exception cref="TimeoutException">クエリが <paramref name="timeout"/> を超過した場合。</exception>
    Task<IReadOnlyList<SeverityCount>> QuerySeverityDistributionAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 観測窓内の受信量上位の送信元（Top talkers）を集計して返す（フラッディング検知。
    /// M8-5/Issue #159）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="QuerySourceActivityAsync"/> との違い</b>: あちらは無音化検出のための
    /// 全送信元・最終受信時刻の古い順の集計であり、**量の上位 N 抽出は不可**（UI-4 の制約。
    /// ui.md §12 UI-4）。本操作はその逆に「量の上位 N」を返す集計であり、UI-4 の制約に
    /// 抵触しないよう<b>別メソッド・ダッシュボードの別セクション</b>として追加する
    /// （無音化検出の全送信元一覧を上位 N 表示に置き換えない）。
    /// </para>
    /// <para>
    /// 窓の必須化の理由は <see cref="QuerySeverityDistributionAsync"/> と同じ
    /// （Issue #145——SourceAddress 列にも索引が無く、無条件集計はフルスキャンへ劣化し得る）。
    /// </para>
    /// <para>
    /// 並び順は件数降順。同数の場合は <see cref="SourceActivity.SourceAddress"/> の
    /// 昇順で決定的にする。
    /// </para>
    /// </remarks>
    /// <param name="from">観測窓の下限（UTC、含む）。</param>
    /// <param name="to">観測窓の上限（UTC、含む）。</param>
    /// <param name="limit">返却する送信元数の上限（必須）。</param>
    /// <param name="timeout">クエリの実行時間上限。</param>
    /// <exception cref="TimeoutException">クエリが <paramref name="timeout"/> を超過した場合。</exception>
    Task<IReadOnlyList<SourceActivity>> QueryTopTalkersAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
