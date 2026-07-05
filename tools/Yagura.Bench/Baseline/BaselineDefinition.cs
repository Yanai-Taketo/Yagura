using System.Text.Json.Serialization;

namespace Yagura.Bench.Baseline;

/// <summary>
/// CI 回帰判定の基準値ファイル（Issue #62。architecture.md §5.2「リポジトリに記録した基準値との比を
/// 判定し、許容帯を超えて劣化したら不合格」の実体）。
/// </summary>
/// <remarks>
/// <para>
/// <b>基準値の更新手続き</b>: 引き下げ方向の更新には理由の分類が必須（実行環境の変更 / 機能追加に
/// よる意図的なトレードオフ）。詳細手順は docs/development/conventions.md「CI 回帰ベンチの基準値
/// 更新」節を参照。本ファイルのコメント（<see cref="BaselineFile.Notes"/> ではなく JSON 側の
/// 生コメント不可のため、このヘッダドキュメントコメントが手順への入口を兼ねる）。
/// </para>
/// <para>
/// <b>M-5（許容帯の確定）は本 Issue のスコープでは未完了</b>: CI 環境（windows-latest）の揺らぎを
/// 複数回実測してから許容帯を確定する、というのが architecture.md §5.2 の要求だが、本 Issue の
/// 実装時点では CI を複数回実行した実測データがない（ローカル実測のみ）。そのため
/// <see cref="BaselineEntry.ToleranceRatio"/> は「仕組みが機能することを確認するための暫定値」
/// （基準比 50% 劣化で fail）を置いている。CI 実測後、本体が値を確定させる（architecture.md §9
/// M-5 行 / 本ファイルの <c>_meta.status</c> 参照）。
/// </para>
/// </remarks>
public sealed class BaselineFile
{
    /// <summary>基準値ファイルのメタ情報（更新履歴・確定状況の記録）。</summary>
    [JsonPropertyName("_meta")]
    public required BaselineMeta Meta { get; init; }

    /// <summary>シナリオ識別子（<see cref="BaselineEntry.ScenarioKey"/>）→ 基準値エントリ。</summary>
    [JsonPropertyName("scenarios")]
    public required IReadOnlyDictionary<string, BaselineEntry> Scenarios { get; init; }
}

/// <summary>基準値ファイルのメタ情報。</summary>
/// <param name="Status">
/// 確定状況（例: "provisional-tolerance-pending-ci-measurement"）。M-5（許容帯確定）前は暫定値である
/// ことを機械可読にも残すためのフィールド。
/// </param>
/// <param name="LastUpdated">最終更新日（YYYY-MM-DD）。</param>
/// <param name="RecordedFrom">
/// 基準値の記録元。CI 実測で確定した基準値は当該 CI run の URL
/// （https://github.com/&lt;owner&gt;/&lt;repo&gt;/actions/runs/&lt;id&gt;）をここに記載する。暫定値の間は
/// ローカル実測の出所（マシン・結果ファイルの所在）を記載する。
/// </param>
/// <param name="UpdateProcedureReference">
/// 更新手続きの参照先（docs/development/conventions.md の該当節）。
/// </param>
public sealed record BaselineMeta(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lastUpdated")] string LastUpdated,
    [property: JsonPropertyName("recordedFrom")] string RecordedFrom,
    [property: JsonPropertyName("updateProcedureReference")] string UpdateProcedureReference);

/// <summary>
/// 1 シナリオの基準値エントリ。
/// </summary>
/// <param name="ScenarioKey">
/// シナリオ識別子（ScenarioReport.ScenarioName と一致させる。同名シナリオを複数構成で使う場合に
/// 備え、ファイル側のキーはシナリオ名そのものでよい——v0.1 スコープでは CI 回帰ベンチは
/// シナリオごとに 1 構成のみ）。
/// </param>
/// <param name="BaselineThroughputPerSecond">
/// 基準スループット（毎秒送出成功数。<see cref="Reporting.ScenarioReport.LoadResult"/> の
/// SucceededCount / Elapsed.TotalSeconds）。
/// </param>
/// <param name="BaselineSavedCount">基準保存件数（<see cref="Reporting.ScenarioReport.Reconciliation"/> の SavedCount）。</param>
/// <param name="ToleranceRatio">
/// 許容帯（相対劣化率。0.5 = 基準比 50% までの劣化を許容し、それを超える劣化で不合格）。
/// <b>M-5 未確定のため暫定値</b>——CI 環境の揺らぎを複数回実測してから確定する
/// （architecture.md §9 M-5）。緩めの暫定値を意図的に置いている（絶対値の合否ではなく
/// 「仕組みが機能すること」の確認が本 Issue のスコープであるため）。
/// </param>
/// <param name="RequireReconciled">
/// 突合成立（<see cref="Verification.ReconciliationResult.IsReconciled"/>）を合否条件に含めるか。
/// 既定 true——回帰ベンチも「損失は必ずどれかのカウンタに計上される」という原則を満たすべきである。
/// </param>
public sealed record BaselineEntry(
    [property: JsonPropertyName("scenarioKey")] string ScenarioKey,
    [property: JsonPropertyName("baselineThroughputPerSecond")] double BaselineThroughputPerSecond,
    [property: JsonPropertyName("baselineSavedCount")] long BaselineSavedCount,
    [property: JsonPropertyName("toleranceRatio")] double ToleranceRatio,
    [property: JsonPropertyName("requireReconciled")] bool RequireReconciled = true);
