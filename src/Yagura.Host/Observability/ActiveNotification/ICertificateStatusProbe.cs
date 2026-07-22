namespace Yagura.Host.Observability.ActiveNotification;

/// <summary>
/// 証明書の現在の状態を照会する契約（PR #224 レビュー指摘 #2・#3 への対応）。管理リスナのリモート
/// HTTPS（ADR-0010 Phase 2 決定 4。<see cref="Yagura.Host.Administration.Https.StoreAdminHttpsCertificateStatusProbe"/>）
/// と TLS 受信（ADR-0019。<c>StoreIngestionTlsCertificateStatusProbe</c>）の 2 実装が本契約を共有するため、
/// 契約名は特定用途に寄せず中立とする。
/// </summary>
/// <remarks>
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
/// 既存の能動通知がそのまま管理リスナ用証明書にも適用される」の実体——EventId 1014）、
/// ②稼働中の証明書異常（ストアからの削除・秘密鍵アクセス不能・期限切れへの遷移）の検知
/// （EventId 1015）。起動時の 1 回きりの解決（EventId 1013 = 起動時に既に解決できない場合の
/// 縮小継続警告）とは独立の、稼働中の周期監視である。上記 EventId は管理 HTTPS 実装
/// （<c>StoreAdminHttpsCertificateStatusProbe</c>）のもの——TLS 受信実装
/// （<c>StoreIngestionTlsCertificateStatusProbe</c>）は同じ役割を EventId 1017 / 1018 で担う。
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
