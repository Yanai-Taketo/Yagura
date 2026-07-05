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
/// <see cref="WriteBatchAsync"/> の呼び出しは、呼び出し側（永続化段）が直列化する
/// ことを前提とし、本インターフェースの実装は複数呼び出し元からの並行呼び出しに
/// 対する排他制御を提供しない。保持期間削除（<see cref="DeleteOlderThanAsync"/>）も
/// 同じ書き込み経路に直列化される想定である。
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
    /// 条件付きの対話的検索（database.md §1.2 契約 4）。条件（時間範囲・送信元・重大度・
    /// 自由文）+ 射影（一覧用の軽量列。先頭 N 文字）+ 結果上限 + タイムアウトを
    /// <see cref="LogQuery"/> で必須引数化する。
    /// </summary>
    /// <remarks>
    /// この防御（上限・タイムアウトの必須化）は UI の対話的検索が対象であり、
    /// database.md §1.2「契約拡張の予約」（一括読み出し・集計）の管理経路には適用しない。
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
}
