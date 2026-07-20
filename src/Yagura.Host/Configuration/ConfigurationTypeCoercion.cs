namespace Yagura.Host.Configuration;

/// <summary>
/// 設定ファイルのスカラー位置に文字列以外の JSON トークン（数値・真偽値）が書かれ、
/// 文字列として受理した「型の読み替え」1 件（configuration.md §1。Issue #334）。
/// </summary>
/// <remarks>
/// <para>
/// 受理は正常系であり<b>警告にしない</b>（§1——手編集した設定は当分そのままであるため、
/// 警告にすると起動のたびに出続けて本当に見るべき警告——未知キー・既定値への差し替え——の
/// 感度を落とす）。情報として起動結果・再読み込み結果の一覧に、未知キーの一覧・既定値へ
/// 差し替えた値の一覧と同じ場所で表示する。
/// </para>
/// <para>
/// 平坦化を経た後は <c>4194304</c> と <c>"4194304"</c> を区別できないため、検出は元の
/// JSON トークン型を見られる読み手（<c>JsonDocument</c> 走査）で行う（§1 の制約 1。
/// <see cref="YaguraConfigurationLoader.DetectTypeCoercions"/>）。
/// </para>
/// </remarks>
/// <param name="Key">読み替えが起きた設定キー（JSON のパス表記。例: <c>Ingestion:Spool:QuotaBytes</c>）。</param>
/// <param name="JsonType">元のトークン型の表示名（「数値」または「真偽値」）。</param>
/// <param name="AppliedValue">文字列として受理した値（構成システムの平坦化結果と同じ表記）。</param>
public sealed record ConfigurationTypeCoercion(string Key, string JsonType, string AppliedValue)
{
    /// <summary>一覧表示・ログ用の 1 行表現。</summary>
    public string ToDisplayString() => $"{Key}（{JsonType} {AppliedValue} を文字列として受理）";
}
