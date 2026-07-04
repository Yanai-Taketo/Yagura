namespace Yagura.Host.Configuration;

/// <summary>
/// 設定ファイルを「保存の下準備」として読み込んだ結果（configuration.md §3
/// 「読み込み → 変更 → 検証 → 保存」の最初の 2 段に対応する）。
/// </summary>
/// <remarks>
/// <see cref="YaguraConfigurationLoader.Load"/> が返す <see cref="ConfigurationLoadResult"/>
/// とは目的が異なる: あちらは検証・環境変数上書き・既定値フォールバックを終えた「起動に使う
/// 実効値」（<see cref="ResolvedYaguraConfiguration"/>）を返すのに対し、本型は
/// 「ファイルにそのまま書かれていた生の値」（<see cref="YaguraConfigurationOptions"/>）と、
/// 保存時の楽観競合検出に使う <see cref="VersionToken"/> を返す。ウィザード・手編集の
/// 保存フローは実効値ではなく生の値を変更対象とする（<see cref="ConfigurationChangePlanner"/>
/// のコメント参照）。
/// </remarks>
/// <param name="Options">ファイルに書かれていた生の値（ファイル不在の場合は既定値のみの空インスタンス）。</param>
/// <param name="VersionToken">保存時に外部変更の有無を検証するためのトークン。</param>
public sealed record YaguraConfigurationFileSnapshot(
    YaguraConfigurationOptions Options,
    ConfigurationVersionToken VersionToken);
