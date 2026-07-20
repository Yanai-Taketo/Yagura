using Microsoft.Extensions.Logging;
using Yagura.Host.Configuration;
using Yagura.Ingestion.Persistence;

namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>
/// メール通知の対象となるイベント ID の明示リスト（ADR-0017 決定 6）。
/// </summary>
/// <remarks>
/// <para>
/// <b>既定は「対象外」</b>。新しいイベント ID を追加する PR は、メールに乗せるかどうかを
/// 明示的に判断して本表と security.md §4.3 の「メール通知対象」マーカー列の両方を更新する
/// （更新義務によって判断を強制する——黙って増える経路を作らない）。
/// </para>
/// <para>
/// <b>重大度を本表が持つ理由</b>（ADR-0017 改訂 1）: 流量上限の対象外か否かは
/// <see cref="Severity"/> で決まる。これを「発火点で使われている <see cref="LogLevel"/>」という
/// 別の場所の暗黙値から読み取る作りにすると、発火点のログレベルを何気なく変えた PR が
/// 流量制御の挙動を黙って変えてしまう。**優先度の挙動は本表だけを読めば決まる**ようにする。
/// </para>
/// <para>
/// <b>メール送信自身の ID（<see cref="EmailNotificationEventIds"/>）は含まない</b>——
/// 送信失敗をメールで通知しようとするループを定義レベルで排除する（決定 5）。
/// </para>
/// </remarks>
internal static class EmailNotificationAllowlist
{
    /// <summary>メール通知における事象の重大度（流量上限の適用可否を決める）。</summary>
    internal enum Severity
    {
        /// <summary>
        /// 警告。全体流量上限（<see cref="EmailNotificationConstants.RateLimitMaxMessages"/> 通/時）の
        /// 対象。上限に達した場合は、未送信のトリガ種の初報を送信済みトリガ種の再送より優先する。
        /// </summary>
        Warning,

        /// <summary>
        /// エラー。全体流量上限の<b>対象外</b>（枠を待たずに送る）。ただし EventId 粒度の
        /// 再送間隔（<see cref="EmailNotificationConstants.ResendInterval"/>）は等しく適用されるため
        /// 無制限ではない。
        /// </summary>
        Error,
    }

    private static readonly IReadOnlyDictionary<int, Severity> SeverityByEventId =
        new Dictionary<int, Severity>
        {
            // --- スプール系（architecture.md §3.2・§4.6） ---
            [ActiveNotificationEventIds.SpoolDegradedStartup.Id] = Severity.Warning,          // 1001
            [ActiveNotificationEventIds.SpoolQuotaNearLimit.Id] = Severity.Warning,           // 1002
            [ActiveNotificationEventIds.SpoolQuotaReached.Id] = Severity.Warning,             // 1003
            [ActiveNotificationEventIds.SpoolEvacuationContinuing.Id] = Severity.Warning,     // 1004
            [PersistenceEventIds.SpoolWriteFailed.Id] = Severity.Warning,                     // 1005（Yagura.Ingestion 発）
            [ActiveNotificationEventIds.MonitoredVolumeFreeSpaceLow.Id] = Severity.Warning,   // 1006
            [ActiveNotificationEventIds.ExpressCapacityNearLimit.Id] = Severity.Warning,      // 1007

            // --- 監視自身の失敗・自己検証（発火点は LogError。ADR-0017 決定 6 の名指し） ---
            [ActiveNotificationEventIds.EvaluationFailed.Id] = Severity.Error,                // 1008
            [ActiveNotificationEventIds.SpoolSelfTestFailed.Id] = Severity.Error,             // 1009
            [ActiveNotificationEventIds.SpoolSelfTestTimeoutBacklog.Id] = Severity.Warning,   // 1010

            // --- 証明書（期限接近・使用不能） ---
            [ActiveNotificationEventIds.AdminHttpsCertificateExpiryApproaching.Id] = Severity.Warning,        // 1014
            [ActiveNotificationEventIds.AdminHttpsCertificateUnavailableWhileRunning.Id] = Severity.Warning,  // 1015
            [ActiveNotificationEventIds.IngestionTlsCertificateExpiryApproaching.Id] = Severity.Warning,      // 1017
            [ActiveNotificationEventIds.IngestionTlsCertificateUnavailableWhileRunning.Id] = Severity.Warning, // 1018

            // --- 認証防御の昇格（ADR-0011。攻撃の予兆——希少で重要な初報の代表例） ---
            [ActiveNotificationEventIds.AdminAuthFailureDefenseEscalated.Id] = Severity.Warning, // 1019

            // --- 受信リスナの縮小継続（Issue #291。「届かない」に直結する） ---
            [ConfigurationEventIds.ListenerBindFailedDegradedStartup.Id] = Severity.Warning,     // 1022

            // --- 送信元の途絶検知（ADR-0018 決定 5——本 ADR が名指しで登録を要求する 2 件。
            //     1029〔復帰〕は対象外: 復帰は対応を要する事象ではなく能動通知しない（決定 3） ---
            [SourceSilence.SourceSilenceEventIds.SourceSilenceDetected.Id] = Severity.Warning,      // 1027
            [SourceSilence.SourceSilenceEventIds.SourceSilenceBurstDetected.Id] = Severity.Warning, // 1028

            // --- 恒久障害による書き込み失敗の開始（ADR-0017 委任 10 の裁定。発火点は LogError） ---
            [PersistenceEventIds.PermanentWriteFailure.Id] = Severity.Error,                        // 1030
        };

    /// <summary>本表に登録済みのすべてのイベント ID。テスト・網羅性検証用。</summary>
    internal static IReadOnlyCollection<int> RegisteredEventIds => (IReadOnlyCollection<int>)SeverityByEventId.Keys;

    /// <summary>
    /// 指定したイベント ID がメール通知の対象かを判定し、対象なら重大度を返す。
    /// </summary>
    internal static bool TryGetSeverity(EventId eventId, out Severity severity) =>
        SeverityByEventId.TryGetValue(eventId.Id, out severity);
}
