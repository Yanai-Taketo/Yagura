using System.Runtime.Versioning;
using Yagura.Host.Observability.ActiveNotification;

namespace Yagura.Host.Administration.Https;

/// <summary>
/// <see cref="ICertificateStatusProbe"/> の Windows 証明書ストア実装
/// （ADR-0010 Phase 2 決定 4。PR #224 レビュー指摘 #2・#3 への対応）。
/// </summary>
/// <remarks>
/// 周期（1 分。<see cref="ActiveNotificationConstants.PollInterval"/>）ごとに
/// <see cref="CertificateProvider.Load"/> で証明書ストアを再照会する——起動時に読み込んだ
/// <c>X509Certificate2</c> インスタンス（Kestrel の <c>ServerCertificateSelector</c> が保持）は
/// ストアからの削除を検知できないため、稼働中の異常（削除・秘密鍵アクセス不能）はこの再照会で
/// 拾う。ストアの読み取り照会は軽量であり、1 分周期のコストは許容範囲
/// （<c>LogStoreExpressCapacityChecker</c> が同じ周期で SQL 問い合わせを行う既存判断と同等）。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StoreAdminHttpsCertificateStatusProbe : ICertificateStatusProbe
{
    private readonly string _normalizedThumbprint;

    /// <param name="normalizedThumbprint">
    /// 正規化済み（大文字・16 進 40 桁）の拇印（<c>YaguraConfigurationLoader</c> が検証済みの値）。
    /// </param>
    public StoreAdminHttpsCertificateStatusProbe(string normalizedThumbprint)
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
