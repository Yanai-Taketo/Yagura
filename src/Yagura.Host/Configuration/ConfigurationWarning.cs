namespace Yagura.Host.Configuration;

/// <summary>
/// 不正値検出時の警告 1 件（configuration.md §1「既定値で継続」「縮小側で継続」の
/// 2 分類が対象。「起動失敗」分類は警告ではなく <see cref="ConfigurationValidationException"/>
/// で表現する）。
/// </summary>
/// <remarks>
/// §1 の要求「キー名・検出した不正値・適用した値の 3 点を明示する」をそのまま
/// フィールド化したもの。ILogger 出力・将来の UI/イベントログ表示（M3-3 以降）の
/// 両方から同じ情報源として参照できるようにする。
/// </remarks>
/// <param name="Key">不正値が検出された設定キー（JSON のパス表記。例: <c>Ingestion:Udp:Port</c>）。</param>
/// <param name="InvalidValue">設定ファイル・環境変数から読み取った不正値の文字列表現。</param>
/// <param name="AppliedValue">フォールバックとして実際に適用した値の文字列表現。</param>
/// <param name="Reason">不正と判定した理由（人間可読。ログメッセージにそのまま使う）。</param>
public sealed record ConfigurationWarning(string Key, string InvalidValue, string AppliedValue, string Reason);
