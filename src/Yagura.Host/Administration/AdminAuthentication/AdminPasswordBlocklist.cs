using System.IO.Compression;
using System.Reflection;

namespace Yagura.Host.Administration.AdminAuthentication;

/// <summary>
/// 既知漏洩パスワード・頻出パターンのブロックリスト突合（ADR-0011 決定 7・委任事項 5）。
/// </summary>
/// <remarks>
/// <para>
/// <b>静的同梱・実行時ネットワーク取得なし</b>（決定 7）: 辞書
/// （<c>PasswordBlocklist/blocklist.txt.gz</c>。出典・ライセンス・加工内容は
/// <c>PasswordBlocklist/PROVENANCE.md</c> 参照）は <c>EmbeddedResource</c> としてアセンブリへ
/// 静的に同梱し、実行時にオンラインの漏洩パスワード DB へ問い合わせる経路は持たない。
/// </para>
/// <para>
/// <b>大文字小文字を区別しない突合</b>: 辞書側は正規化時に小文字化済みのため、判定側も
/// <see cref="IsBlocked"/> で入力を小文字化してから突合する——<c>Password1234</c> のような
/// 大文字化だけの回避を許さない。
/// </para>
/// <para>
/// <b>読み込みは初回アクセス時に一度だけ行う</b>（<see cref="Lazy{T}"/>。97,746 件の
/// <see cref="HashSet{T}"/> 構築はミリ秒オーダーだが、プロセス起動のたびに全ログイン試行で
/// 読み直す必要はない）。
/// </para>
/// </remarks>
public static class AdminPasswordBlocklist
{
    private const string ResourceName = "Yagura.Host.Administration.AdminAuthentication.PasswordBlocklist.blocklist.txt.gz";

    private static readonly Lazy<HashSet<string>> Entries = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>収録件数（テスト・診断用）。</summary>
    public static int Count => Entries.Value.Count;

    /// <summary>
    /// 指定したパスワードが既知漏洩・頻出パターンのブロックリストに含まれるかどうか
    /// （大文字小文字を区別しない）。
    /// </summary>
    public static bool IsBlocked(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Entries.Value.Contains(password.ToLowerInvariant());
    }

    private static HashSet<string> Load()
    {
        var assembly = typeof(AdminPasswordBlocklist).Assembly;
        using var resourceStream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"埋め込みパスワードブロックリストが見つかりません: {ResourceName}" +
                "（Yagura.Host.csproj の EmbeddedResource 設定を確認してください）。");

        using var gzip = new GZipStream(resourceStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, System.Text.Encoding.UTF8);

        var result = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length > 0)
            {
                result.Add(line);
            }
        }

        return result;
    }
}
