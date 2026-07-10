using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Yagura.Host.Administration.Https;

/// <summary>
/// 管理リスナのリモート HTTPS 証明書（ADR-0010 Phase 2 決定 4）の秘密鍵読み取り権限を、
/// サービスアカウントへ付与する。configuration.md §6 が確定済みの方式（証明書の選択時に、
/// サービスアカウントへ当該証明書の秘密鍵の読み取り権限<b>のみ</b>を付与する——広い権限へ逃げない）
/// をリモート管理 HTTPS 証明書にも適用する。
/// </summary>
/// <remarks>
/// <para>
/// <b>対象は CNG（Cryptography API: Next Generation）秘密鍵に限る</b>: Windows 8 / Server 2012
/// 以降、証明書の取り込み（証明書スナップイン・AD CS のクライアント発行手順・
/// <c>CertificateRequest</c> API による自己署名等）で作成される秘密鍵は既定で CNG ベース
/// （<see cref="RSACng"/>/<see cref="ECDsaCng"/>）であり、鍵コンテナは
/// <c>%ProgramData%\Microsoft\Crypto\Keys\&lt;UniqueName&gt;</c> にファイルとして存在する
/// （Microsoft Learn "Key Storage and Retrieval" の既定のマシンキーセットの説明に基づく設計。
/// ソフトウェア KSP 以外——スマートカード・HSM・TPM 保護鍵等——はファイルとして存在しないため
/// 本メソッドの対象外とし、その場合は明示的な失敗理由を返す（CF-D2 の手動手順への誘導が
/// フォールバックになる。configuration.md §6 の「主理由の限界」と同じ誠実さで、自動化できない
/// 範囲を隠さない）。レガシー CAPI（<see cref="RSACryptoServiceProvider"/>）鍵は対象外とする——
/// .NET 10 上の新規証明書取り込み・AD CS の既定発行では CNG が既定のため、対応の優先度を CNG に
/// 絞ることは configuration.md §6 の対象読者（AD はあるが AD CS 未導入の環境を含む一般的な
/// Windows 管理者）にとって現実的な範囲である。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class AdminCertificatePrivateKeyAccessGranter
{
    /// <summary>
    /// 指定した証明書の秘密鍵に対する読み取り専用アクセスを <paramref name="accountName"/> へ付与する。
    /// </summary>
    /// <param name="certificate">秘密鍵を持つ証明書（<see cref="X509Certificate2.HasPrivateKey"/> が true であること）。</param>
    /// <param name="accountName">
    /// 付与先アカウント（例: <c>NT SERVICE\Yagura</c>——ADR-0004 決定 4 の仮想サービスアカウント）。
    /// </param>
    public static AdminCertificatePrivateKeyGrantResult TryGrantReadAccess(X509Certificate2 certificate, string accountName)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        if (!certificate.HasPrivateKey)
        {
            return AdminCertificatePrivateKeyGrantResult.Failure("証明書に秘密鍵がありません。");
        }

        var keyFilePath = ResolveCngKeyFilePath(certificate);
        if (keyFilePath is null)
        {
            return AdminCertificatePrivateKeyGrantResult.Failure(
                "秘密鍵が CNG ソフトウェアキーストレージプロバイダー（ファイルベース）ではないため、" +
                "自動での権限付与に対応していません（スマートカード・HSM・TPM 保護鍵等が該当します）。" +
                "証明書スナップイン（certlm.msc）の「秘密キーの管理」から手動で権限を付与してください" +
                "（configuration.md §6 CF-D2）。");
        }

        if (!File.Exists(keyFilePath))
        {
            return AdminCertificatePrivateKeyGrantResult.Failure(
                $"秘密鍵ファイルが見つかりません（想定パス: {keyFilePath}）。証明書の取り込み方法を確認してください。");
        }

        try
        {
            var account = new NTAccount(accountName);
            var fileInfo = new FileInfo(keyFilePath);
            var accessControl = fileInfo.GetAccessControl();
            accessControl.AddAccessRule(new FileSystemAccessRule(
                account,
                FileSystemRights.Read,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            fileInfo.SetAccessControl(accessControl);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IdentityNotMappedException or IOException)
        {
            return AdminCertificatePrivateKeyGrantResult.Failure(
                $"秘密鍵ファイル {keyFilePath} への ACL 付与に失敗しました: {ex.Message}");
        }

        return AdminCertificatePrivateKeyGrantResult.Success(keyFilePath);
    }

    /// <summary>
    /// 証明書の秘密鍵（RSA/ECDsa）が CNG（ソフトウェアキーストレージプロバイダー）であれば、
    /// 対応する鍵コンテナファイルの絶対パスを返す。それ以外（レガシー CAPI・スマートカード等）は
    /// <see langword="null"/> を返す。
    /// </summary>
    private static string? ResolveCngKeyFilePath(X509Certificate2 certificate)
    {
        using (var rsa = certificate.GetRSAPrivateKey())
        {
            if (rsa is RSACng rsaCng)
            {
                return BuildCngKeyPath(rsaCng.Key.UniqueName);
            }
        }

        using (var ecdsa = certificate.GetECDsaPrivateKey())
        {
            if (ecdsa is ECDsaCng ecdsaCng)
            {
                return BuildCngKeyPath(ecdsaCng.Key.UniqueName);
            }
        }

        return null;
    }

    private static string? BuildCngKeyPath(string? uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName))
        {
            return null;
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "Microsoft", "Crypto", "Keys", uniqueName);
    }
}

/// <summary><see cref="AdminCertificatePrivateKeyAccessGranter.TryGrantReadAccess"/> の結果。</summary>
public sealed class AdminCertificatePrivateKeyGrantResult
{
    private AdminCertificatePrivateKeyGrantResult(bool succeeded, string? keyFilePath, string? failureReason)
    {
        Succeeded = succeeded;
        KeyFilePath = keyFilePath;
        FailureReason = failureReason;
    }

    public bool Succeeded { get; }

    public string? KeyFilePath { get; }

    public string? FailureReason { get; }

    public static AdminCertificatePrivateKeyGrantResult Success(string keyFilePath) => new(true, keyFilePath, null);

    public static AdminCertificatePrivateKeyGrantResult Failure(string reason) => new(false, null, reason);
}
