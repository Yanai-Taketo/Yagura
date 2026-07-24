namespace Yagura.Host.Observability.ActiveNotification;

/// <summary>
/// サーバ証明書の現在の状態を照会する契約（PR #224 レビュー指摘 #2・#3 への対応）。
/// </summary>
/// <remarks>
/// <para>
/// <b>用途は 1 つではない</b>（#359 で命名を中立化した）。実装は 2 つあり、
/// <see cref="ActiveNotificationMonitor"/> が 2 系統を別々に結線する:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>管理リスナのリモート HTTPS</b>（ADR-0010 Phase 2 決定 4）——
/// <c>Yagura.Host.Administration.Https.StoreAdminHttpsCertificateStatusProbe</c>。
/// 期限接近の事前警告 = EventId 1014・稼働中の使用不能検知 = EventId 1015。
/// </description></item>
/// <item><description>
/// <b>TLS 受信</b>（RFC 5425。security.md §6）——
/// <c>Yagura.Host.Ingestion.Tls.StoreIngestionTlsCertificateStatusProbe</c>。
/// 期限接近の事前警告 = EventId 1017・稼働中の使用不能検知 = EventId 1018。
/// </description></item>
/// </list>
/// <para>
/// <b>なぜ抽象化するか</b>: 実装（証明書ストアの照会——
/// <c>Yagura.Host.Administration.Https.CertificateProvider</c>）は Windows 専用 API
/// （<c>[SupportedOSPlatform("windows")]</c>）であり、<see cref="ActiveNotificationMonitor"/>
/// 自体はプラットフォーム注釈を持たない。合成ルート（<c>Program</c>。Windows 専用と宣言済み）が
/// Windows 実装を構築して渡し、監視側は契約のみを参照する——<see cref="IExpressCapacityChecker"/>
/// と同じ分離パターン。テストでは偽実装に差し替えて決定的に検証する。
/// </para>
/// <para>
/// <b>役割</b>: ①期限接近の事前警告（ADR-0010 決定 4「期限接近の事前警告は configuration.md §6
/// 既存の能動通知がそのまま管理リスナ用証明書にも適用される」の実体）、
/// ②稼働中の証明書異常（ストアからの削除・秘密鍵アクセス不能・期限切れへの遷移）の検知。
/// 起動時の 1 回きりの解決（EventId 1013 / 1016 = 起動時に既に解決できない場合の
/// 縮小継続警告）とは独立の、稼働中の周期監視である。
/// </para>
/// </remarks>
public interface ICertificateStatusProbe
{
    /// <summary>
    /// 証明書の現在の状態を照会する。呼び出しは <see cref="ActiveNotificationMonitor"/> の
    /// 周期（<see cref="ActiveNotificationConstants.PollInterval"/>）ごとに 1 回。
    /// </summary>
    CertificateStatus Check();
}

/// <summary>
/// <see cref="ICertificateStatusProbe.Check"/> の結果。
/// </summary>
/// <param name="IsAvailable">
/// 証明書が現在も解決できる（ストアに存在し、秘密鍵を持つ）か。<see langword="false"/> は
/// 稼働中にストアから削除された・秘密鍵にアクセスできなくなった等の異常を示す。
/// </param>
/// <param name="NotAfter">
/// 証明書の有効期限（<see cref="IsAvailable"/> が <see langword="true"/> のときのみ有効）。
/// 期限接近（<see cref="ActiveNotificationConstants.CertificateExpiryWarningWindow"/>）・
/// 期限切れの判定入力。
/// </param>
/// <param name="FailureReason">
/// <see cref="IsAvailable"/> が <see langword="false"/> の場合の理由（通知本文に含める。
/// 秘密情報は含まない）。
/// </param>
public sealed record CertificateStatus(
    bool IsAvailable,
    DateTimeOffset NotAfter,
    string? FailureReason);
