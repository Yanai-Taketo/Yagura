using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yagura.Host.Configuration;

/// <summary>
/// 設定ファイルのスカラー値を、JSON のトークン型によらず文字列として読み取る変換器。
/// </summary>
/// <remarks>
/// <para>
/// configuration.md §1 の不変条件「<c>yagura.json</c> を読む経路は複数あってよいが、受理範囲は
/// 一致していなければならない」を満たすために置く。基準は .NET 構成システム（<c>AddJsonFile</c>）の
/// 受理範囲であり、本変換器はその挙動を <see cref="JsonSerializer"/> 側で再現する。
/// </para>
/// <para>
/// <b>なぜ必要か</b>: <see cref="YaguraConfigurationOptions"/> は全キーを <c>string?</c> で受け取る
/// （解析と検証は各キーの担当が行う——§8）。<c>AddJsonFile</c> は数値・真偽値を文字列へ平坦化して
/// 渡すため元から通るが、<see cref="JsonSerializer"/> の既定は型が一致しないと
/// <see cref="JsonException"/> を投げる。この差により、手編集で <c>"QuotaBytes": 4194304</c> と
/// 数値を書いた設定ファイルがサービスを起動不能にする退行が生じていた（Issue #312）。
/// </para>
/// <para>
/// <b>変換規則は実測に基づく</b>（2026-07-18 に <c>Microsoft.Extensions.Configuration.Json</c> 10.0.10 で確認）。
/// 特に真偽値が <c>"True"</c> / <c>"False"</c>（先頭大文字）になる点と、数値が表記のまま
/// （<c>514.0</c> → <c>"514.0"</c>）保たれる点は、推測で実装すると一致しない。
/// </para>
/// <list type="table">
///   <listheader><term>JSON</term><description>構成システムの結果 = 本変換器の結果</description></listheader>
///   <item><term><c>4194304</c></term><description><c>"4194304"</c>（表記のまま）</description></item>
///   <item><term><c>514.0</c></term><description><c>"514.0"</c>（表記のまま）</description></item>
///   <item><term><c>true</c> / <c>false</c></term><description><c>"True"</c> / <c>"False"</c></description></item>
///   <item><term><c>null</c></term><description><see langword="null"/>（キー欠落と同じ扱い）</description></item>
///   <item><term><c>[]</c></term><description><c>""</c>（空文字と区別できない。§1 はこれを不正値として扱うと定める）</description></item>
///   <item><term><c>{}</c></term><description><see langword="null"/>（キー欠落と区別できない。§1 はこの限界を明示的に受け入れる）</description></item>
///   <item><term>中身のある配列・オブジェクト</term><description><see langword="null"/>（子キーへ展開され、親キー自体は値を持たない）</description></item>
/// </list>
/// </remarks>
internal sealed class ConfigurationValueStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),

            // 表記をそのまま保つ（514.0 を "514" に正規化しない）。構成システムは生テキストを渡すため、
            // 数値として解釈し直すと表記が変わり受理範囲が一致しなくなる。
            JsonValueKind.Number => element.GetRawText(),

            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Null => null,

            // スカラーを期待する位置に構造が来た場合。構成システムは子キーへ展開するため
            // 親キー自体は値を持たない（= null）。ただし空配列だけは空文字として現れる。
            JsonValueKind.Array => element.GetArrayLength() == 0 ? string.Empty : null,
            JsonValueKind.Object => null,

            _ => throw new JsonException($"設定値として解釈できない JSON 値です: {element.ValueKind}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        // 書き込みは常に文字列（保存側の表現は文字列で統一する。§8）。
        writer.WriteStringValue(value);
    }
}
