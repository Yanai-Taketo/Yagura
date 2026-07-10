using System.Runtime.Versioning;
using Yagura.Host.Administration.Https;
using Yagura.Host.Observability.ActiveNotification;

namespace Yagura.Host.Ingestion.Tls;

/// <summary>
/// TLS 受信（RFC 5425。opt-in。security.md §6。Issue #137）の証明書の周期監視実装。
/// </summary>
/// <remarks>
/// <para>
/// <b>証明書ストア参照ロジックの共有</b>: 参照方式は Web UI の HTTPS・管理リスナのリモート
/// HTTPS と同型（security.md §6「参照方式は Web UI の HTTPS と同型」）——本クラスは
/// <see cref="AdminCertificateProvider.Load"/> をそのまま呼ぶ（TLS 受信専用の証明書ロード実装を
/// 別途持たない。重複実装を避ける）。契約（<see cref="IAdminHttpsCertificateStatusProbe"/>）も
/// 管理リスナ側と共有する——「証明書ストアから拇印で 1 件解決し、現在の有効/期限を返す」という
/// 形は両者で同一であり、TLS 受信専用の別インターフェースを新設する理由が無い。
/// </para>
/// <para>
/// <b>周期監視の目的が異なる</b>点は <see cref="ActiveNotificationMonitor"/> 側の評価メソッド
/// （<c>EvaluateIngestionTlsCertificate</c>）が担う——管理リスナ側は期限切れで新規 HTTPS
/// ハンドシェイクを拒否する状態の通知だが、TLS 受信側は「止めない」設計（security.md §6）のため、
/// 本プローブが返す状態は純粋に可視化目的であり、リスナの受理可否には影響しない。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StoreIngestionTlsCertificateStatusProbe : IAdminHttpsCertificateStatusProbe
{
    private readonly string _normalizedThumbprint;

    /// <param name="normalizedThumbprint">
    /// 正規化済み（大文字・16 進 40 桁）の拇印（<c>YaguraConfigurationLoader</c> が検証済みの値）。
    /// </param>
    public StoreIngestionTlsCertificateStatusProbe(string normalizedThumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedThumbprint);
        _normalizedThumbprint = normalizedThumbprint;
    }

    public AdminHttpsCertificateStatus Check()
    {
        var result = AdminCertificateProvider.Load(_normalizedThumbprint);

        if (!result.Succeeded)
        {
            return new AdminHttpsCertificateStatus(
                IsAvailable: false,
                NotAfter: default,
                FailureReason: result.FailureReason);
        }

        return new AdminHttpsCertificateStatus(
            IsAvailable: true,
            NotAfter: new DateTimeOffset(result.Certificate!.NotAfter),
            FailureReason: null);
    }
}
