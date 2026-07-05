using System.Globalization;

namespace Yagura.Storage.Spool;

/// <summary>
/// セグメントファイルの命名規則。ファイル名の辞書順ソートがそのまま生成順（= drain が
/// 消化すべき古い順）に一致するようにする。
/// </summary>
/// <remarks>
/// <para>
/// <b>命名</b>: <c>{UTC Ticks の 19 桁ゼロ埋め}-{連番 4 桁}-{Guid 下 8 桁}.seg</c>。
/// Ticks を先頭に置くことで生成時刻順の辞書順ソートを保証し、同一 Ticks 内での
/// 衝突（高頻度なセグメント切替）を連番で解決する。末尾の Guid 断片は同一プロセス内
/// での一意性を保証する（連番のみだと再起動をまたいだ場合に衝突し得るため）。
/// </para>
/// <para>
/// <b>消化済みの識別</b>: drain 中のセグメントは <c>.draining</c> 拡張子を追加した
/// 一時名へリネームしてから読む（読み取り中に同名ファイルが誤って二重 drain
/// されることを防ぐ——本プロセス内は drain ループを単一にすることで足りるが、
/// ファイル名からも状態が読み取れるようにしておくと障害調査時に分かりやすいため）。
/// </para>
/// </remarks>
internal static class SpoolSegmentFileNames
{
    private const string SegmentExtension = ".seg";
    private const string DrainingExtension = ".draining";

    public static string CreateSegmentFileName(DateTimeOffset createdAt, int sequence)
    {
        var ticks = createdAt.UtcDateTime.Ticks.ToString("D19", CultureInfo.InvariantCulture);
        var seq = sequence.ToString("D4", CultureInfo.InvariantCulture);
        var guidFragment = Guid.NewGuid().ToString("N")[..8];
        return $"{ticks}-{seq}-{guidFragment}{SegmentExtension}";
    }

    public static bool IsSegmentFile(string fileName) =>
        fileName.EndsWith(SegmentExtension, StringComparison.Ordinal);

    public static string ToDrainingName(string segmentPath) => segmentPath + DrainingExtension;
}
