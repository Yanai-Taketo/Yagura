using System.Text.RegularExpressions;

namespace Yagura.Web.ForwarderKit;

/// <summary>
/// MSI 検出の判定（ファイル名パターン一致・版比較・公式ハッシュ照合）を担う純粋関数群
/// （ADR-0008 設計条件 9・委任 #7）。列挙処理（<see cref="SystemForwarderMsiSource"/>）から
/// 分離し、単体テストで境界を固定できるようにする（<see cref="NicCandidateFilter"/> と同じ設計）。
/// </summary>
public static partial class ForwarderMsiFilter
{
    /// <summary>
    /// <see cref="ForwarderMsiConstraints.FileNamePattern"/> のコンパイル済み表現
    /// （大文字小文字を区別しない——Windows のファイル名は大文字小文字を区別しないため）。
    /// </summary>
    [GeneratedRegex(@"^fluent-bit-.*-win64\.msi$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNamePatternRegex();

    /// <summary>指定したファイル名が配置フォルダの検出対象パターンに一致するか。</summary>
    public static bool IsCandidateFileName(string fileName) =>
        !string.IsNullOrEmpty(fileName) && FileNamePatternRegex().IsMatch(fileName);

    /// <summary>
    /// ファイル名から版を抽出する（<c>fluent-bit-4.0.14-win64.msi</c> → <c>4.0.14</c>）。
    /// ProductVersion が取得できない場合の補助手段——<b>ファイル名だけに依拠しない</b>という
    /// ADR-0008 設計条件 9 の意図により、これは「ProductVersion 優先」の補助にとどめる。
    /// </summary>
    public static string? ExtractVersionFromFileName(string fileName)
    {
        var match = FileNameVersionRegex().Match(fileName);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^fluent-bit-(.+)-win64\.msi$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameVersionRegex();

    /// <summary>
    /// 同梱対象 MSI の版（ProductVersion 優先・取得不能時はファイル名由来を補助的に使う）を解決する。
    /// </summary>
    public static string? ResolveEffectiveVersion(string? productVersion, string fileName) =>
        !string.IsNullOrWhiteSpace(productVersion) ? productVersion : ExtractVersionFromFileName(fileName);

    /// <summary>
    /// 実効版が検証済み版と一致するか（大文字小文字を区別しない文字列比較。版番号の意味論的
    /// 比較（セマンティックバージョニング）はここでは行わない——検証済み版の表明はあくまで
    /// 単一の固定文字列であり、一致 / 不一致の 2 値判定で十分——ADR-0008 設計条件 9）。
    /// </summary>
    public static bool MatchesVerifiedVersion(string? effectiveVersion, string verifiedVersion) =>
        effectiveVersion is not null &&
        string.Equals(effectiveVersion, verifiedVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 公式配布 SHA256 との照合結果（3 値: 一致 / 不一致 / 未確認——公式ハッシュ定数が
    /// 未設定の場合は「未確認」とし、誤って不一致と表示しない。ADR-0008 設計条件 9）。
    /// </summary>
    public static OfficialHashMatchResult MatchesOfficialHash(string actualSha256, string? officialSha256)
    {
        if (string.IsNullOrWhiteSpace(officialSha256))
        {
            return OfficialHashMatchResult.Unverified;
        }

        return string.Equals(actualSha256, officialSha256, StringComparison.OrdinalIgnoreCase)
            ? OfficialHashMatchResult.Match
            : OfficialHashMatchResult.Mismatch;
    }
}

/// <summary>公式配布 SHA256 との照合結果（ADR-0008 設計条件 9）。</summary>
public enum OfficialHashMatchResult
{
    /// <summary>公式ハッシュ定数が未設定のため照合していない。</summary>
    Unverified,

    /// <summary>公式ハッシュと一致した。</summary>
    Match,

    /// <summary>公式ハッシュと一致しなかった。</summary>
    Mismatch,
}
