using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Yagura.Host.Administration.Https;

/// <summary>
/// 管理リスナのリモート HTTPS（ADR-0010 Phase 2 決定 4）用証明書を、Windows 証明書ストア
/// （ローカルコンピューター・<c>My</c>）から拇印で参照する。
/// </summary>
/// <remarks>
/// configuration.md §6（閲覧 UI の HTTPS）と同型の参照方式——PFX ファイルパス + パスワード方式は
/// 採らない（ファイルとパスワードの管理という新しい漏洩面を作らない）。証明書の生成支援・
/// インストールは行わない（利用者の持ち込みを基本とする——同 §6 の既存方針をそのまま踏襲）。
/// </remarks>
[SupportedOSPlatform("windows")]
public static class AdminCertificateProvider
{
    /// <summary>
    /// 指定した拇印の証明書をローカルコンピューターの <c>My</c> ストアから読み込む。
    /// </summary>
    /// <param name="normalizedThumbprint">
    /// 正規化済み（大文字・16 進 40 桁）の拇印。<see cref="Yagura.Host.Configuration.YaguraConfigurationLoader"/>
    /// が既に形式検証済みの値を渡す想定。
    /// </param>
    public static AdminCertificateLoadResult Load(string normalizedThumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedThumbprint);

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);

        try
        {
            store.Open(OpenFlags.ReadOnly);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        {
            return AdminCertificateLoadResult.Failure(
                $"証明書ストア（LocalMachine\\My）を開けませんでした: {ex.Message}");
        }

        // validOnly: false — 期限切れ・信頼チェーン不備の証明書も一旦取得し、呼び出し側
        // （Program）が「期限切れは縮小継続（loopback は影響を受けない）」の判断をできるようにする
        // （configuration.md §6 の既存方針と同じ「停止する・HTTP へは落とさない」をリモート面のみへ
        // 適用するための前提）。
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalizedThumbprint, validOnly: false);

        if (matches.Count == 0)
        {
            return AdminCertificateLoadResult.Failure(
                $"拇印 {normalizedThumbprint} の証明書が LocalMachine\\My ストアに見つかりません。");
        }

        var certificate = matches[0];

        if (!certificate.HasPrivateKey)
        {
            return AdminCertificateLoadResult.Failure(
                $"拇印 {normalizedThumbprint} の証明書には秘密鍵がありません（またはこのアカウントから" +
                "アクセスできません）。証明書の取り込み時に秘密鍵をエクスポート可能として取り込んだか確認してください。");
        }

        var now = DateTime.Now;
        var isExpired = now < certificate.NotBefore || now > certificate.NotAfter;

        return AdminCertificateLoadResult.Success(certificate, isExpired);
    }
}

/// <summary>
/// <see cref="AdminCertificateProvider.Load"/> の結果。
/// </summary>
public sealed class AdminCertificateLoadResult
{
    private AdminCertificateLoadResult(X509Certificate2? certificate, bool isExpired, string? failureReason)
    {
        Certificate = certificate;
        IsExpired = isExpired;
        FailureReason = failureReason;
    }

    /// <summary>読み込めた証明書（失敗時は <see langword="null"/>）。</summary>
    public X509Certificate2? Certificate { get; }

    /// <summary>
    /// 証明書が有効期間外（期限切れ、または <c>NotBefore</c> 未到達）かどうか。
    /// <see cref="Succeeded"/> が <see langword="true"/> のときのみ意味を持つ。
    /// </summary>
    public bool IsExpired { get; }

    /// <summary>失敗理由（成功時は <see langword="null"/>）。</summary>
    public string? FailureReason { get; }

    /// <summary>証明書の取得自体（ストア参照・秘密鍵の存在確認）に成功したか。期限切れでも <see langword="true"/>。</summary>
    public bool Succeeded => Certificate is not null;

    public static AdminCertificateLoadResult Success(X509Certificate2 certificate, bool isExpired) =>
        new(certificate, isExpired, failureReason: null);

    public static AdminCertificateLoadResult Failure(string reason) =>
        new(certificate: null, isExpired: false, failureReason: reason);
}
