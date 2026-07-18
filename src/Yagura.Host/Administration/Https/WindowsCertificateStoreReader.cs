using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yagura.Abstractions.Administration;

namespace Yagura.Host.Administration.Https;

/// <summary>
/// <see cref="ICertificateStoreReader"/> の Windows 実体（ADR-0012 決定 2）。
/// <c>LocalMachine\My</c> を <c>OpenFlags.ReadOnly</c> で開いて全件を走査し、serverAuth EKU +
/// 秘密鍵ありの証明書を最小フィールドの DTO で返す。<see cref="AdminCertificateProvider"/> と同層。
/// </summary>
/// <remarks>
/// 列挙は BCL の <see cref="X509Store"/>／<see cref="X509Certificate2"/> のみで完結し、新規 NuGet
/// 依存を追加しない（ADR-0012 検討した選択肢 (D)）。秘密鍵 ACL 付与
/// （<see cref="AdminCertificatePrivateKeyAccessGranter"/>）は本クラスからは呼ばない——lab 実測で
/// サービスアカウントには付与権限（WRITE_DAC）がないと確定したため、UI は読取検証と誘導に留める
/// （ADR-0012 決定 3 = (b)）。<see cref="IsPrivateKeyReadable"/> の読取検証は現在の実行アカウント
/// （= サービスアカウント <c>NT SERVICE\Yagura</c>）の視点で行われる。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsCertificateStoreReader : ICertificateStoreReader
{
    /// <summary>サーバ認証の拡張キー使用法（Enhanced Key Usage）の OID。</summary>
    internal const string ServerAuthEkuOid = "1.3.6.1.5.5.7.3.1";

    public IReadOnlyList<CertificateCandidate> ListServerAuthCertificates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        var now = DateTimeOffset.Now;
        var candidates = new List<CertificateCandidate>();

        foreach (var certificate in store.Certificates)
        {
            try
            {
                // serverAuth EKU + 秘密鍵ありに最小化（用途違いの誤選択・機内 PKI 露出の抑制。
                // ADR-0012 決定 2）。いずれも満たさないものは選択不能なので列挙から除外する。
                if (!certificate.HasPrivateKey || !HasServerAuthEku(certificate))
                {
                    continue;
                }

                candidates.Add(ToCandidate(certificate, now));
            }
            finally
            {
                certificate.Dispose();
            }
        }

        // 決定的な並び（有効期限の新しい順 → 拇印順）。UI の表示順を実行ごとにブレさせない。
        return candidates
            .OrderByDescending(c => c.NotAfter)
            .ThenBy(c => c.Thumbprint, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 証明書が serverAuth EKU（<see cref="ServerAuthEkuOid"/>）を明示的に持つかを判定する。
    /// </summary>
    /// <remarks>
    /// RFC 5280 上、EKU 拡張を持たない証明書は「あらゆる用途に有効」とみなされ得るが、ADR-0012
    /// 決定 2 は列挙を「serverAuth を明示する証明書」へ最小化する方針のため、<b>EKU 拡張なし・
    /// serverAuth を含まない EKU 拡張ありのいずれも対象外</b>とする（用途が確認できない証明書を
    /// 管理 HTTPS 用として勧めない）。
    /// </remarks>
    internal static bool HasServerAuthEku(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509EnhancedKeyUsageExtension eku)
            {
                foreach (var oid in eku.EnhancedKeyUsages)
                {
                    if (string.Equals(oid.Value, ServerAuthEkuOid, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                // EKU 拡張はあるが serverAuth を含まない → 用途限定が serverAuth と不一致。
                return false;
            }
        }

        // EKU 拡張そのものが無い → serverAuth を明示していないため対象外。
        return false;
    }

    /// <summary>
    /// 証明書を表示用の最小メタ DTO へ写像する（期限・秘密鍵読取可否の判定を含む）。
    /// </summary>
    internal static CertificateCandidate ToCandidate(X509Certificate2 certificate, DateTimeOffset now)
    {
        var notBefore = new DateTimeOffset(certificate.NotBefore);
        var notAfter = new DateTimeOffset(certificate.NotAfter);
        var isExpired = now < notBefore || now > notAfter;

        return new CertificateCandidate(
            Thumbprint: certificate.Thumbprint,
            SubjectCommonName: GetSubjectCommonName(certificate),
            Issuer: certificate.Issuer,
            NotBefore: notBefore,
            NotAfter: notAfter,
            IsExpired: isExpired,
            IsPrivateKeyReadable: IsPrivateKeyReadable(certificate));
    }

    /// <summary>サブジェクトの CN を取り出す。取れなければサブジェクト DN 全体を返す。</summary>
    private static string GetSubjectCommonName(X509Certificate2 certificate)
    {
        var cn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrEmpty(cn) ? certificate.Subject : cn;
    }

    /// <summary>
    /// 現在の実行アカウント（サービスアカウント）が当該秘密鍵を実際に読み取れるかを検証する。
    /// </summary>
    /// <remarks>
    /// <see cref="X509Certificate2.HasPrivateKey"/> が true でも、秘密鍵ファイルの ACL にアカウントが
    /// 含まれなければ鍵ハンドルの取得は失敗する（lab 実測: <c>NTE_BAD_KEYSET 0x80090016</c>）。
    /// 「UI では秘密鍵ありと見えたが、サービスアカウントからは読めず再起動後に縮小継続」という
    /// 乖離を保存前に可視化するための読取検証（ADR-0012 決定 3・受け入れ基準の「読取検証」）。
    /// internal なのは、保存前 fail-closed 検証（<see cref="AdminRemoteAccessAdminService"/>。
    /// ADR-0012 決定 4）が列挙 UI と同一の読取検証を共有するため（判定ロジックの二重実装をしない）。
    /// </remarks>
    internal static bool IsPrivateKeyReadable(X509Certificate2 certificate)
    {
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            if (rsa is not null)
            {
                _ = rsa.KeySize;
                return true;
            }

            using var ecdsa = certificate.GetECDsaPrivateKey();
            if (ecdsa is not null)
            {
                _ = ecdsa.KeySize;
                return true;
            }

            return false;
        }
        catch (CryptographicException)
        {
            // 鍵セットにアクセスできない（ACL 不足・非ファイル鍵等）。読取不可として扱う。
            return false;
        }
    }
}
