namespace Yagura.Storage;

/// <summary>
/// 製品自身が生成するシステムイベント（database.md §2.3）。
/// 利用者のログとは別の論理テーブルに保存する。
/// </summary>
/// <param name="Kind">種別（受信断・保持期間削除の実行記録等）。</param>
/// <param name="StartAt">区間の開始時刻（UTC）。</param>
/// <param name="EndAt">区間の終了時刻（UTC）。</param>
/// <param name="Approximate">近似断点か（architecture.md §4.4 の印）。</param>
/// <param name="Id">レコード識別子。provider が採番するため、挿入前のレコードでは <c>null</c> を許容する。</param>
/// <param name="Details">付帯情報（削除実行なら削除件数等）。</param>
public sealed record SystemEvent(
    string Kind,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    bool Approximate,
    long? Id = null,
    string? Details = null);
