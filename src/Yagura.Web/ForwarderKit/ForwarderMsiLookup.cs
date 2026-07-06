namespace Yagura.Web.ForwarderKit;

/// <summary>
/// <see cref="IForwarderMsiSource.Lookup"/> の結果種別（ADR-0008 設計条件 9）。
/// </summary>
public enum ForwarderMsiLookupState
{
    /// <summary>配置フォルダに一致する MSI が無い（フォルダ自体が無い場合も含む）。</summary>
    NotFound,

    /// <summary>配置フォルダに一致する MSI が複数ある（既定でエラー停止——安全側）。</summary>
    Multiple,

    /// <summary>配置フォルダに一致する MSI がちょうど 1 件ある（同梱可能）。</summary>
    Single,
}

/// <summary>
/// 単一検出時の MSI 詳細（ADR-0008 設計条件 9）。
/// </summary>
/// <param name="FilePath">MSI のフルパス。</param>
/// <param name="FileName">MSI のファイル名（拡張子込み）。</param>
/// <param name="ProductVersion">
/// MSI の ProductVersion（取得できた場合）。取得不能時は <see langword="null"/>——
/// その場合はファイル名から抽出した版を補助的に使う（<see cref="ForwarderMsiFilter"/> 参照）。
/// </param>
/// <param name="Sha256">MSI バイト列の SHA256（16 進小文字）。</param>
/// <param name="Length">MSI のバイト長。</param>
public sealed record ForwarderMsiDetails(
    string FilePath,
    string FileName,
    string? ProductVersion,
    string Sha256,
    long Length);

/// <summary>
/// 配置フォルダの検出結果（判別可能な型。ADR-0008 設計条件 9）。
/// </summary>
/// <param name="State">検出状態。</param>
/// <param name="Details"><see cref="ForwarderMsiLookupState.Single"/> のときのみ非 null。</param>
/// <param name="MultipleCandidateFileNames">
/// <see cref="ForwarderMsiLookupState.Multiple"/> のときの検出ファイル名一覧（画面の一覧表示用）。
/// </param>
/// <remarks>
/// プロパティ名を <c>Single</c> ではなく <see cref="Details"/> とする——静的ファクトリメソッド
/// <see cref="Single(ForwarderMsiDetails)"/> と同名にすると <c>error CS0102</c>
/// （型に同名メンバーが重複）になるため。
/// </remarks>
public sealed record ForwarderMsiLookup(
    ForwarderMsiLookupState State,
    ForwarderMsiDetails? Details = null,
    IReadOnlyList<string>? MultipleCandidateFileNames = null)
{
    /// <summary>未検出の結果を作る。</summary>
    public static ForwarderMsiLookup NotFound() => new(ForwarderMsiLookupState.NotFound);

    /// <summary>複数検出の結果を作る。</summary>
    public static ForwarderMsiLookup Multiple(IReadOnlyList<string> candidateFileNames) =>
        new(ForwarderMsiLookupState.Multiple, MultipleCandidateFileNames: candidateFileNames);

    /// <summary>単一検出の結果を作る。</summary>
    public static ForwarderMsiLookup Single(ForwarderMsiDetails details) =>
        new(ForwarderMsiLookupState.Single, Details: details);
}
