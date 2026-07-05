using Yagura.Ingestion.Diagnostics;

namespace Yagura.Host.Observability;

/// <summary>
/// メタデータ領域（architecture.md §4.3）に永続化する内容そのもの。
/// カウンタ累積値・停止イベント・生存時刻の 3 要素を 1 ファイルにまとめる
/// （§4.3「カウンタ累積値・停止イベント・生存時刻を保存する…独立ローカルファイル」）。
/// </summary>
/// <param name="Counters">
/// <see cref="IngestionMetrics"/> のカウンタ累積値（プロセス再起動をまたいで継続する値）。
/// </param>
/// <param name="LastStopEvent">
/// 直近の正常停止の記録（§1.3 手順 3「正常停止イベントを記録して終了する」）。
/// <c>null</c> は「前回終了時に正常停止イベントを書けなかった」ことを表す——初回起動、または
/// 前回がクラッシュ（強制終了）だった場合にこの状態になる。§4.4 の「前回が正常停止でない場合」
/// の判定はこのフィールドの有無で行う。
/// </param>
/// <param name="LastLivenessAt">
/// 直近の生存時刻更新（§4.4「稼働中は一定間隔で生存時刻をメタデータ領域に更新する」）。
/// 正常停止時は手順 1・3 でも更新されるため、正常停止であれば <see cref="LastStopEvent"/> と
/// ほぼ同時刻になる。<c>null</c> は生存時刻の更新が一度も行われていない（メタデータ領域自体が
/// 初回作成である）ことを表す。
/// </param>
public sealed record MetadataState(
    IngestionCounterSnapshot Counters,
    StopEventRecord? LastStopEvent,
    DateTimeOffset? LastLivenessAt)
{
    /// <summary>メタデータ領域がまだ存在しない（初回起動）場合の既定状態。</summary>
    public static MetadataState Initial { get; } = new(IngestionCounterSnapshot.Zero, null, null);
}

/// <summary>
/// 正常停止イベントの記録（architecture.md §1.3 手順 1・3、§4.4）。
/// </summary>
/// <param name="ReceiveSocketClosedAt">
/// 手順 1「受信ソケットを閉じ」た時刻（UTC）。§4.4 の受信断区間の開始時刻はこの値を使う
/// （停止処理全体の終了時刻ではなく、受信が止まった瞬間を区間の開始とするため）。
/// </param>
/// <param name="StoppedAt">
/// 手順 3「正常停止イベントを記録して終了する」時点の時刻（UTC）。停止処理全体の完了時刻。
/// </param>
public sealed record StopEventRecord(DateTimeOffset ReceiveSocketClosedAt, DateTimeOffset StoppedAt);
