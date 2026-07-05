namespace Yagura.Web.Components.Common;

/// <summary>
/// 欠けているデータの明示（ui.md §5.3）の種別——「無い」の理由を画面で見分けられるようにする。
/// </summary>
public enum YaguraMissingDataKind
{
    /// <summary>受信断区間（architecture.md §4.4。画面の言葉: 受信できなかった時間帯）。</summary>
    ReceptionOutage,

    /// <summary>クラッシュ由来の近似断点を含む受信断区間（近似である旨を印す。ui.md §5.3）。</summary>
    ReceptionOutageApproximate,

    /// <summary>保持地平（database.md §2.3）: 保持期間より古い範囲は削除済み。</summary>
    RetentionHorizon,

    /// <summary>検索の打ち切り（architecture.md §6）: 結果上限・タイムアウトによる打ち切り。</summary>
    SearchTruncated,
}
