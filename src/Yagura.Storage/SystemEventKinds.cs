namespace Yagura.Storage;

/// <summary>
/// システムイベント（database.md §2.3）の Kind 値のうち、複数モジュールが参照するもの。
/// </summary>
/// <remarks>
/// 受信断 2 種の値の発行元は Yagura.Host（<c>ObservabilityConstants</c>——同名の const が
/// 本クラスを別名参照する）だが、閲覧 UI（M8-3 の状態画面・検索画面）が表示の平易語対応
/// （ui.md §7）のために同じ値を参照する必要があり、Web → Host の参照は持てない参照構造
/// （architecture.md §1.1）のため、値の置き場を横断契約側（Yagura.Storage——SystemEvent 型と
/// 同居）に置く。保持期間削除の Kind は <see cref="RetentionConstants.SystemEventKindRetentionDelete"/>
/// を正とする（M5-1 から存在する定数の置き場を変えない）。
/// </remarks>
public static class SystemEventKinds
{
    /// <summary>正常停止による受信断区間（architecture.md §4.4「正常停止」）。</summary>
    public const string DowntimeNormalStop = "downtime.normal-stop";

    /// <summary>クラッシュ由来の近似断点による受信断区間（architecture.md §4.4「クラッシュ」）。</summary>
    public const string DowntimeCrashApproximate = "downtime.crash-approximate";

    /// <summary>
    /// リスナ再構成（設定ライブ再読み込みによる bind の張り替え。CF-4 層2。Issue #262）に
    /// 伴う受信断区間。プロセス跨ぎの 2 種と異なり稼働中に区間が確定するため、再構成の完了時に
    /// 直接書き込まれる（起動時の区間変換を経ない）。通常は 1 秒未満の瞬断。
    /// </summary>
    public const string DowntimeListenerReconfigure = "downtime.listener-reconfigure";

    /// <summary>
    /// 起動時（または再構成失敗後）に bind できなかったリスナが、CF-6 の定期再試行で受信を
    /// 再開するまでの受信断区間（Issue #291。#141 原子的起動の反転——2026-07-16 オーナー裁定。
    /// 区間の開始 = bind を最初に試みて失敗した時刻、終了 = 再試行が成功して受信を再開した時刻）。
    /// </summary>
    public const string DowntimeListenerBindRetry = "downtime.listener-bind-retry";

    /// <summary>保持期間削除の実行記録（database.md §3。値の正は RetentionConstants）。</summary>
    public const string RetentionDelete = RetentionConstants.SystemEventKindRetentionDelete;

    /// <summary>受信断系の Kind か（状態画面の履歴の仕分けに使う。M8-3）。</summary>
    public static bool IsDowntime(string kind) =>
        kind is DowntimeNormalStop or DowntimeCrashApproximate or DowntimeListenerReconfigure
            or DowntimeListenerBindRetry;
}
