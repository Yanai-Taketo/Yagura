using Microsoft.Extensions.Logging;

namespace Yagura.Ingestion.Persistence;

/// <summary>
/// 永続化段（<see cref="PersistenceWriter"/>）が発火する Windows イベントログ ID
/// （architecture.md §4.6 の能動通知。security.md §4.3「1000 番台 = 運用警告」区画）。
/// </summary>
/// <remarks>
/// 番号の正本は security.md §4.3 のイベント ID 一覧表であり、本クラスはその実装側の反映。
/// 1000 番台の他の ID（1001〜1004・1006〜1007）は
/// <c>Yagura.Host.Observability.ActiveNotification.ActiveNotificationEventIds</c> に定義する
/// （発火箇所が Yagura.Host 側のため）。1005 のみ発火箇所が本プロジェクト
/// （<see cref="PersistenceWriter.EvacuateSingleRecordAsync"/>）であり、Yagura.Host →
/// Yagura.Ingestion という既存の参照方向を逆転させられないため、ここに定義する
/// （<c>ActiveNotificationEventIds</c> の remarks も参照）。
/// </remarks>
public static class PersistenceEventIds
{
    /// <summary>スプールへの書き込みがリトライ後も失敗した（architecture.md §3.2.1・§4.6）。レベル: 警告。</summary>
    public static readonly EventId SpoolWriteFailed = new(1005, "SpoolWriteFailed");
}
