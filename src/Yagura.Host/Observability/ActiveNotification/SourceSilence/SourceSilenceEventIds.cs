using Microsoft.Extensions.Logging;

namespace Yagura.Host.Observability.ActiveNotification.SourceSilence;

/// <summary>
/// 送信元の途絶検知（ADR-0018 委任 1）のイベント ID。番号の正本は security.md §4.3 の表。
/// </summary>
/// <remarks>
/// 採番は 1027 以降（1025・1026 は ADR-0017 のメール通知チャネルが使用済み）。
/// <b>1027・1028 はメール通知の対象</b>（ADR-0018 決定 5——ADR-0017 が accepted のため
/// allowlist へ登録する）。<b>1029〔復帰〕は対象外</b>——復帰は対応を要する事象ではなく
/// 能動通知しない（決定 3。Issue #132 の前例）。
/// </remarks>
public static class SourceSilenceEventIds
{
    /// <summary>
    /// 登録済み送信元からの受信が閾値を超えて途絶した（ADR-0018 決定 3）。レベル: 警告。
    /// </summary>
    public static readonly EventId SourceSilenceDetected = new(1027, "SourceSilenceDetected");

    /// <summary>
    /// 同一評価周期に閾値件数以上の送信元が同時に途絶した（ADR-0018 決定 3 の一斉集約）。
    /// レベル: 警告。
    /// </summary>
    /// <remarks>
    /// 集約スイッチ障害等で 50 台が同時に黙ったとき、個別 50 件（メール接続時は 50 通）は
    /// 診断情報としても劣化している——1 件にまとめ、サーバ側受信経路の確認へ誘導する。
    /// </remarks>
    public static readonly EventId SourceSilenceBurstDetected = new(1028, "SourceSilenceBurstDetected");

    /// <summary>
    /// 途絶していた送信元からの受信が再開した（ADR-0018 決定 3）。レベル: <b>情報</b>。
    /// </summary>
    /// <remarks>
    /// <b>1000 番台に情報レベルを置く初例</b>（security.md §4.3 は 1000 番台を「運用警告」と
    /// 括っている）。管理操作ではないため 2000 番台にも属さず、事象の性質としては運用側に
    /// 属するため本区画に置く。能動通知はしない——復帰は対応を要する事象ではない（Issue #132
    /// の前例）。それでも記録を残すのは、<b>途絶警告（始端）と対で「ログが欠けていた期間」の
    /// 終端を証跡に残す</b>ためである（外部送信元のログ欠落期間は監査上の関心事であり、
    /// サーバ内部の自己回復とは監査対象性が異なる）。
    /// </remarks>
    public static readonly EventId SourceSilenceRecovered = new(1029, "SourceSilenceRecovered");
}
