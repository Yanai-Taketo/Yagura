namespace Yagura.Storage;

/// <summary>
/// 送信元別の受信状況の集計行（database.md §1.2「契約拡張の予約」(b) 集計の実体化。M8-3）。
/// ダッシュボードの送信元別受信状況（ui.md §4・UI-4 無音化検出）の入力。
/// </summary>
/// <remarks>
/// UI-4 の制約「量の上位 N のみの表示は不可——無音化した送信元の検出（最終受信時刻の古い順の
/// 全送信元一覧）を必須要件とする」を集計側で成立させるため、並び順は
/// <see cref="LastReceivedAt"/> の昇順（古い順 = 無音の疑いが強い順）とする
/// （<see cref="ILogStore.QuerySourceActivityAsync"/> の契約）。
/// </remarks>
/// <param name="SourceAddress">送信元アドレス。</param>
/// <param name="LastReceivedAt">この送信元からの最終受信時刻（UTC。ReceivedAt の最大値）。</param>
/// <param name="RecordCount">この送信元の保存済みレコード件数。</param>
public sealed record SourceActivity(
    string SourceAddress,
    DateTimeOffset LastReceivedAt,
    long RecordCount);
