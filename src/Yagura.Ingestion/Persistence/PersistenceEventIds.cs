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

    /// <summary>
    /// 恒久障害（設定・スキーマ・権限——再試行で解消しない失敗）によりバッチ書き込みが失敗し、
    /// スプールへの退避を開始した（database.md §1.2 契約 3。ADR-0017 委任 10。Issue #369）。
    /// レベル: エラー。
    /// </summary>
    /// <remarks>
    /// 従来は EventId なし（= 0）で書き出されており、イベントログでの機械照合ができず、
    /// メール通知（ADR-0017）も構造的に捕捉できなかった（プロバイダは EventId 0 を対象外と
    /// する契約）。恒久障害の開始は「放置すればスプールが満ちる」導火線の点火であり、
    /// 1004（退避継続）経由の間接検知は継続判定（仮値 5 分）の後にしか出ず根本原因（DB 側の
    /// 恒久失敗）も示さないため、採番して直接通知の対象にする——委任 10 の裁定。
    /// </remarks>
    public static readonly EventId PermanentWriteFailure = new(1030, "PermanentWriteFailure");

    /// <summary>
    /// 稼働中の Windows 統合認証での DB 接続失敗によりバッチ書き込みが失敗した
    /// （ADR-0015 決定 5 の観測性要件。database.md §6.1。Issue #418）。レベル: エラー。
    /// </summary>
    /// <remarks>
    /// 統合認証の接続失敗と分類できた恒久障害では、1030 の代わりに本 ID で**実行主体
    /// （実効実行アカウント名）と失敗種別（DC 起因 / SQL Server 起因の一次切り分け）**を残す
    /// ——同一例外で 1030 と二重警告にしない（抑制窓も 1030 と共有する。
    /// <see cref="PersistenceWriter"/> 参照）。分類根拠は SEC-14 (a)/(c) の AD lab 実測
    /// （2026-07-24。ADR-0015 改訂履歴 3）。
    /// </remarks>
    public static readonly EventId IntegratedAuthConnectionFailed = new(1031, "IntegratedAuthConnectionFailed");
}
