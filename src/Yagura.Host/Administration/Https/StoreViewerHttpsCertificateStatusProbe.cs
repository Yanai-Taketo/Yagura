using System.Runtime.Versioning;
using Yagura.Host.Observability.ActiveNotification;

namespace Yagura.Host.Administration.Https;

/// <summary>
/// 閲覧 UI HTTPS 証明書用の <see cref="ICertificateStatusProbe"/> の Windows 証明書ストア実装
/// （ADR-0022 決定 2 可視化①・決定 7。Issue #455 段階 ③。
/// <see cref="StoreAdminHttpsCertificateStatusProbe"/> の閲覧版——3 例目）。
/// </summary>
/// <remarks>
/// 周期（1 分。<see cref="ActiveNotificationConstants.PollInterval"/>）ごとに
/// <see cref="CertificateProvider.Load"/> で証明書ストアを再照会する——起動時に読み込んだ
/// <c>X509Certificate2</c> インスタンス（Kestrel の <c>ServerCertificateSelector</c> が保持）は
/// ストアからの削除を検知できないため、稼働中の異常（削除・秘密鍵アクセス不能）はこの再照会で
/// 拾う（管理リモート HTTPS・TLS 受信の既存 2 プローブと同一の判断）。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StoreViewerHttpsCertificateStatusProbe : ICertificateStatusProbe
{
    private readonly string _normalizedThumbprint;

    /// <param name="normalizedThumbprint">
    /// 正規化済み（大文字・16 進 40 桁）の拇印（<c>YaguraConfigurationLoader</c> が検証済みの値）。
    /// </param>
    public StoreViewerHttpsCertificateStatusProbe(string normalizedThumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedThumbprint);
        _normalizedThumbprint = normalizedThumbprint;
    }

    public CertificateStatus Check()
    {
        var result = CertificateProvider.Load(_normalizedThumbprint);

        if (!result.Succeeded)
        {
            return new CertificateStatus(
                IsAvailable: false,
                NotAfter: default,
                FailureReason: result.FailureReason);
        }

        return new CertificateStatus(
            IsAvailable: true,
            NotAfter: new DateTimeOffset(result.Certificate!.NotAfter),
            FailureReason: null);
    }
}
