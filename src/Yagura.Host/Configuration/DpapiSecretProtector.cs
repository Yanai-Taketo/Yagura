using System.Security.Cryptography;
using System.Text;

namespace Yagura.Host.Configuration;

/// <summary>
/// DPAPI（<see cref="DataProtectionScope.LocalMachine"/> + 用途別 entropy）による秘密値の
/// 暗号化・復号の共通実装（ADR-0004 決定 5 の様式）。
/// </summary>
/// <remarks>
/// <para>
/// <b>用途ごとに entropy を分ける</b>: 本クラスは entropy を引数で受け取り、用途別の薄い
/// ラッパ（<see cref="DpapiConnectionStringProtector"/>・<see cref="DpapiEmailPasswordProtector"/>）が
/// それぞれの名前空間を与える。これにより、ある用途の暗号化表現を別用途の復号経路へ
/// 持ち込んでも復号できない（取り違えが構造的に失敗する）。
/// </para>
/// <para>
/// <b>entropy は秘密ではない</b>: OSS のソースコードに含まれるため秘密性はない。目的は
/// (1) 同一マシン上の無関係なプロセスが entropy なしの <c>ProtectedData.Unprotect</c> で
/// 復号することを防ぐ、(2) 表現形式を版管理できるようにする（将来の形式変更時に接頭辞または
/// entropy の版を上げて共存させる）、(3) 上記の用途間分離——の 3 点である。
/// </para>
/// <para>
/// <b>復号失敗を例外にしない</b>: <see cref="TryUnprotect"/> は改ざん・別マシンへのコピー・
/// entropy 不一致・Base64 不正のいずれも <see langword="false"/> で返す。呼び出し側が
/// 用途ごとの縮退（SQL Server なら SQLite へ縮小 + 警告、メール通知なら送信停止 + 警告）を
/// 決められるようにするため。
/// </para>
/// </remarks>
internal static class DpapiSecretProtector
{
    /// <summary>暗号化表現を示す接頭辞（用途によらず共通）。</summary>
    internal const string Prefix = "dpapi:";

    /// <summary>値が暗号化表現（<see cref="Prefix"/> 付き）かどうかを判別する。</summary>
    internal static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// 平文を DPAPI（LocalMachine + <paramref name="entropyNamespace"/>）で暗号化し、
    /// <c>dpapi:&lt;Base64&gt;</c> 表現を返す。
    /// </summary>
    /// <param name="plaintext">平文（null/空白は不可）。</param>
    /// <param name="entropyNamespace">用途別の entropy 名前空間（版番号を含む）。</param>
    /// <exception cref="PlatformNotSupportedException">Windows 以外の OS で呼ばれた場合。</exception>
    /// <exception cref="CryptographicException">DPAPI の暗号化自体が失敗した場合。</exception>
    internal static string Protect(string plaintext, string entropyNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(entropyNamespace);

        if (!OperatingSystem.IsWindows())
        {
            // 本製品は Windows 専用（ADR-0001）。CA1416 を満たすためのガードであり、
            // 実運用でこの分岐に入ることはない。
            throw new PlatformNotSupportedException("DPAPI 暗号化は Windows でのみ利用できます。");
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            Encoding.UTF8.GetBytes(entropyNamespace),
            DataProtectionScope.LocalMachine);

        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// 暗号化表現を復号する。接頭辞なし・Base64 不正・DPAPI 復号失敗（改ざん・別マシンへの
    /// コピー・entropy 不一致）のいずれも <see langword="false"/> を返す（例外を漏らさない）。
    /// </summary>
    internal static bool TryUnprotect(string? value, string entropyNamespace, out string? plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entropyNamespace);

        plaintext = null;

        if (!IsProtected(value) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(value![Prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            var plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                Encoding.UTF8.GetBytes(entropyNamespace),
                DataProtectionScope.LocalMachine);
            plaintext = Encoding.UTF8.GetString(plainBytes);
            return true;
        }
        catch (CryptographicException)
        {
            // 改ざん・他マシンで暗号化された値・entropy 不一致。復号失敗として扱う。
            return false;
        }
    }
}

/// <summary>
/// メール通知の SMTP パスワード（<c>Notification:Email:Smtp:Password</c>）の DPAPI 保護
/// （ADR-0017 決定 3）。
/// </summary>
/// <remarks>
/// entropy 名前空間は SQL Server 接続文字列（<see cref="DpapiConnectionStringProtector"/>）と
/// <b>意図的に分離する</b>——一方の暗号化表現をもう一方の設定キーへ貼り付けても復号できず、
/// 取り違えが構造的に失敗する。復号失敗時の扱いは呼び出し側の責務であり、本用途では
/// <b>送信停止 + 警告</b>（fail-notify。無認証送信へ黙って落とさない。ADR-0017 決定 3）。
/// </remarks>
internal static class DpapiEmailPasswordProtector
{
    /// <summary>
    /// entropy 名前空間（ADR-0017 決定 3 が指定した値。版番号付き）。
    /// <b>値を変更すると既存の暗号化表現が全て復号不能になる</b>ため、変更は表現形式の
    /// 版上げとしてのみ行うこと。
    /// </summary>
    private const string EntropyNamespace = "Yagura:Notification:Email:Smtp:Password:v1";

    /// <summary>値が暗号化表現かどうかを判別する。</summary>
    internal static bool IsProtected(string? value) => DpapiSecretProtector.IsProtected(value);

    /// <summary>平文のパスワードを暗号化して <c>dpapi:&lt;Base64&gt;</c> 表現を返す。</summary>
    internal static string Protect(string plaintext) =>
        DpapiSecretProtector.Protect(plaintext, EntropyNamespace);

    /// <summary>暗号化表現を復号する（失敗は例外ではなく <see langword="false"/>）。</summary>
    internal static bool TryUnprotect(string? value, out string? plaintext) =>
        DpapiSecretProtector.TryUnprotect(value, EntropyNamespace, out plaintext);
}
