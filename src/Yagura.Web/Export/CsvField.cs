namespace Yagura.Web.Export;

/// <summary>
/// CSV フィールド 1 個のエスケープ（RFC 4180 準拠）と CSV インジェクション対策（Issue #157。
/// ログ検索結果の CSV エクスポートで監査提出・Excel 取り込みに使われることを想定した対策）。
/// </summary>
/// <remarks>
/// <para>
/// <b>RFC 4180</b>: フィールドがカンマ・二重引用符・復帰（CR）・改行（LF）のいずれかを含む場合、
/// フィールド全体を二重引用符で囲み、内部の二重引用符は 2 個に二重化する（RFC 4180 §2 規則 5〜7。
/// https://www.rfc-editor.org/rfc/rfc4180）。
/// </para>
/// <para>
/// <b>CSV インジェクション対策</b>: フィールドの先頭文字が <c>=</c>・<c>+</c>・<c>-</c>・<c>@</c>
/// のいずれかである場合、表計算ソフト（Excel 等）がセルの内容を数式として解釈し、任意コマンド実行
/// につながり得る（OWASP CSV Injection）。対策として先頭に <c>'</c>（シングルクォート）を付与し、
/// 表計算ソフトに文字列として扱わせる。タブ（0x09）・復帰（CR, 0x0D）で始まる値も同じ理由で対象に
/// 含める（OWASP CSV Injection Cheat Sheet が挙げる先頭文字の集合）。
/// </para>
/// <para>
/// <b>適用順序</b>: インジェクション対策の <c>'</c> 付与を先に行い、その後で RFC 4180 の引用要否を
/// 判定する（付与後の値にカンマ等が含まれる場合は当然に引用対象へ含める）。
/// </para>
/// <para>
/// <b>再取り込みは対象外</b>: 本エクスポートは監査提出・閲覧・他ツール取り込みが用途であり、
/// 出力した CSV を Yagura へ再取り込む経路は存在しない。したがって付与した <c>'</c> を復元する
/// 逆変換は提供しない（一方向のエクスポート専用エスケープ）。
/// </para>
/// </remarks>
public static class CsvField
{
    /// <summary>
    /// CSV インジェクション対策として先頭に <c>'</c> を付与する対象の先頭文字
    /// （<c>=</c>・<c>+</c>・<c>-</c>・<c>@</c>・タブ・復帰。クラス remarks 参照）。
    /// </summary>
    private static readonly char[] InjectionPrefixChars = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>RFC 4180 の引用が必要になる文字（二重引用符・カンマ・復帰・改行）。</summary>
    private static readonly char[] QuotingRequiredChars = ['"', ',', '\r', '\n'];

    /// <summary>
    /// 値を CSV フィールドとしてエスケープする。<see langword="null"/> は空フィールド
    /// （<c>""</c>）として扱う——「値なし」を表す独自プレースホルダ（例: <c>—</c>）は使わない
    /// （機械可読性を優先する。UI 表示の慣習とは別）。
    /// </summary>
    public static string Escape(string? value)
    {
        var text = value ?? string.Empty;

        if (text.Length > 0 && Array.IndexOf(InjectionPrefixChars, text[0]) >= 0)
        {
            text = "'" + text;
        }

        if (text.IndexOfAny(QuotingRequiredChars) < 0)
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
