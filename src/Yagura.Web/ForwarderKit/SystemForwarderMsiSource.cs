using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Yagura.Web.ForwarderKit;

/// <summary>
/// 配置フォルダ（データルート配下 <c>forwarder</c>）を実際に読み取る
/// <see cref="IForwarderMsiSource"/> の実体（ADR-0008 設計条件 9・委任 #7）。
/// 列挙・ハッシュ計算・版取得の I/O のみを担い、判定（パターン一致・版比較・ハッシュ照合）は
/// <see cref="ForwarderMsiFilter"/>（純粋関数）に委譲する——<see cref="SystemNicCandidateSource"/> /
/// <see cref="NicCandidateFilter"/> と同じ設計。
/// </summary>
/// <remarks>
/// <para>
/// データルートの実パスは Host 側（<c>YaguraConfigurationLoader.ResolveDataRoot</c>）が知っており、
/// Web 層は直接知らない（<c>INicCandidateSource</c> と同じ参照構造）。そのため本クラスは
/// コンストラクタ引数でフォルダのフルパスを受け取り、Host の DI 登録（<c>Program.cs</c>）で
/// 実パスを注入する。
/// </para>
/// <para>
/// <see cref="SupportedOSPlatformAttribute"/>: MSI の ProductVersion 取得に <c>msi.dll</c>
/// （Windows Installer。Windows 専用）を P/Invoke するため付与する。Yagura は Windows ネイティブな
/// syslog 集約サーバであり（CLAUDE.md・ADR-0001）、<c>Yagura.Host.Program</c> と同じ判断
/// （製品方針そのものを表明する属性として使う。CA1416 抑制が目的ではない）。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SystemForwarderMsiSource : IForwarderMsiSource
{
    public SystemForwarderMsiSource(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        FolderPath = folderPath;
    }

    /// <inheritdoc/>
    public string FolderPath { get; }

    /// <inheritdoc/>
    public ForwarderMsiLookup Lookup(ForwarderMsiArchitecture architecture)
    {
        if (!Directory.Exists(FolderPath))
        {
            return ForwarderMsiLookup.NotFound();
        }

        var candidates = Directory.EnumerateFiles(FolderPath)
            .Where(path => ForwarderMsiFilter.IsCandidateFileName(Path.GetFileName(path), architecture))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            return ForwarderMsiLookup.NotFound();
        }

        if (candidates.Count > 1)
        {
            return ForwarderMsiLookup.Multiple(candidates.Select(Path.GetFileName).ToList()!);
        }

        var filePath = candidates[0];
        var details = ReadDetails(filePath);
        return ForwarderMsiLookup.Single(details);
    }

    private static ForwarderMsiDetails ReadDetails(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var productVersion = TryReadProductVersion(filePath);
        var (sha256, length) = ComputeSha256AndLength(filePath);

        return new ForwarderMsiDetails(filePath, fileName, productVersion, sha256, length);
    }

    /// <summary>
    /// MSI の版を取得する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>設計条件 9 は「MSI の ProductVersion を優先」を要求する</b>が、ProductVersion は
    /// Windows Installer プロパティテーブル（MSI 自体は OLE 構造化ストレージ）に格納されており、
    /// 汎用のファイルバージョンリソース（<see cref="FileVersionInfo"/>）としては公開されていない。
    /// 正攻法は <c>msi.dll</c> の <c>MsiOpenDatabase</c> + <c>MsiDatabaseOpenView</c>
    /// （<c>SELECT Value FROM Property WHERE Property='ProductVersion'</c>）の P/Invoke だが、
    /// COM 相当の複雑な後始末（ハンドルの確実な解放）を要し本 PR の範囲では過剰と判断した。
    /// 汎用のファイルバージョンリソース API（<c>System.Diagnostics.FileVersionInfo</c>）は
    /// 実行可能ファイル（EXE/DLL）のリソースセクションを読むものであり、MSI（OLE 構造化
    /// ストレージ）には使えない。
    /// </para>
    /// <para>
    /// <b>実装した方式</b>: <c>MsiGetFileVersionW</c>（<c>msi.dll</c>）は MSI ファイル自身に対しても
    /// 呼び出すことができ、MSI の <c>ProductVersion</c> プロパティ相当の値を直接返す
    /// （Windows Installer SDK のドキュメント上の契約——インストールされた実行可能ファイルだけでなく
    /// パッケージファイル自体の版取得にも使える）。P/Invoke 1 関数のみで完結し、
    /// <c>MsiOpenDatabase</c> 系のハンドル管理を避けられるため、これを採用する。
    /// 呼び出しが失敗した場合（0 以外の戻り値）は <see langword="null"/> を返し、
    /// 呼び出し側はファイル名からの版抽出（<see cref="ForwarderMsiFilter.ExtractVersionFromFileName"/>）
    /// を補助的に使う——「ファイル名だけに依拠しない」という設計条件 9 の意図は、
    /// ProductVersion 取得を最初に試みる本メソッドの存在そのもので満たす。
    /// </para>
    /// </remarks>
    private static string? TryReadProductVersion(string filePath) =>
        // 実装は ForwarderMsiProductVersionReader へ一本化（ADR-0020 実装時の抽出。
        // 検出側とアップロード側で版判定の読み取り経路を食い違わせない）。
        ForwarderMsiProductVersionReader.TryRead(filePath);

    private static (string Sha256, long Length) ComputeSha256AndLength(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        var sha256 = Convert.ToHexStringLower(hashBytes);
        return (sha256, stream.Length);
    }
}
