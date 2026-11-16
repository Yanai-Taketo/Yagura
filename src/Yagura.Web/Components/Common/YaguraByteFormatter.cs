using System.Globalization;

namespace Yagura.Web.Components.Common;

/// <summary>
/// バイト数の表示整形（画面ごとに書式を持たない——同じ量が画面によって違う桁・単位で見えないようにする）。
/// </summary>
public static class YaguraByteFormatter
{
    /// <summary>1024 進で GB・MB・KB・B のいずれかに整形する（小数 1 桁まで）。</summary>
    public static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"),
        >= 1024L * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        >= 1024L => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };
}
