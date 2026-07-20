namespace Yagura.Host.Configuration;

/// <summary>
/// 設定読み込み 1 回分の結果一式。configuration.md §1 の要求——不正値の警告 3 点
/// （キー・不正値・適用値）と未知キー一覧——を、起動時ログと将来の UI/イベントログ表示
/// （M3-3 以降）の両方が同じ情報源から参照できるようにまとめる。
/// </summary>
/// <remarks>
/// 「起動失敗」分類は本結果に含まれない——検証の時点で <see cref="ConfigurationValidationException"/>
/// を送出し、ホストの起動そのものを失敗させるため、収集結果として残す対象ではない
/// （§1 の 3 分類のうち後段 2 つ「既定値で継続」「縮小側で継続」のみが本型の対象）。
/// </remarks>
/// <param name="Configuration">検証・上書き適用済みの最終設定値。</param>
/// <param name="Warnings">既定値継続・縮小継続で発生した警告の一覧（発生順）。</param>
/// <param name="UnknownKeys">
/// 設定ファイル内で認識されなかったキー（JSON のパス表記）の一覧。§1「未知のキーは警告して
/// 無視する」に対応する。設定ファイルが存在しない場合は常に空。
/// </param>
/// <param name="TypeCoercions">
/// スカラー位置に数値・真偽値のトークンが書かれ、文字列として受理したキー（型の読み替え）の
/// 一覧（情報レベル。§1。Issue #334）。不正値の警告・未知キーとして既に報告されたキーは
/// 含まない（同じキーを二重に報告しない）。設定ファイルが存在しない場合は常に空。
/// </param>
public sealed record ConfigurationLoadResult(
    ResolvedYaguraConfiguration Configuration,
    IReadOnlyList<ConfigurationWarning> Warnings,
    IReadOnlyList<string> UnknownKeys,
    IReadOnlyList<ConfigurationTypeCoercion> TypeCoercions);
