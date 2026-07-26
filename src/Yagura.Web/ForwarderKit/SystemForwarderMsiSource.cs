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
    /// <b>設計条件 9 は「MSI の ProductVersion を優先」を要求する</b>。ProductVersion は
    /// Windows Installer プロパティテーブル（MSI 自体は OLE 構造化ストレージ）に格納されており、
    /// <c>msi.dll</c> の <c>MsiOpenDatabase</c>（読み取り専用）+ Property テーブル参照で読む
    /// （方式の詳細・<c>MsiGetFileVersion</c> を使ってはならない理由 = Issue #436 は
    /// <see cref="ForwarderMsiProductVersionReader"/> を参照）。汎用のファイルバージョンリソース
    /// API（<c>System.Diagnostics.FileVersionInfo</c>）は実行可能ファイル（EXE/DLL）の
    /// リソースセクションを読むものであり、MSI には使えない。
    /// </para>
    /// <para>
    /// 読み取れなかった場合は <see langword="null"/> を返し、呼び出し側はファイル名からの版抽出
    /// （<see cref="ForwarderMsiFilter.ExtractVersionFromFileName"/>）を補助的に使う——
    /// 「ファイル名だけに依拠しない」という設計条件 9 の意図は、ProductVersion 取得を最初に
    /// 試みる本メソッドの存在そのもので満たす。
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
