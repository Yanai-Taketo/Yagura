namespace Yagura.Storage.Spool;

/// <summary>
/// スプールのセグメントファイルへ書き込む 1 レコード（architecture.md §3.2.1）。
/// </summary>
/// <param name="Kind">レコード種別（通常ログ / 自己検証用合成レコード）。</param>
/// <param name="LogRecord">
/// <see cref="SpoolRecordKind.Normal"/> のときの本体。<see cref="LogRecord.Id"/> は
/// provider 採番前提のため常に <c>null</c> のまま書く（drain 時に再採番される）。
/// </param>
/// <param name="SelfTestMarker">
/// <see cref="SpoolRecordKind.SelfTest"/> のときの照合用マーカー文字列（§3.2.5）。
/// 通常ログのときは <c>null</c>。
/// </param>
public sealed record SpoolRecord(
    SpoolRecordKind Kind,
    LogRecord? LogRecord,
    string? SelfTestMarker)
{
    /// <summary>
    /// 通常ログレコードから <see cref="SpoolRecord"/> を作る。
    /// </summary>
    public static SpoolRecord ForLog(LogRecord logRecord) =>
        new(SpoolRecordKind.Normal, logRecord, SelfTestMarker: null);

    /// <summary>
    /// 自己検証用の合成レコードを作る（§3.2.5）。
    /// </summary>
    public static SpoolRecord ForSelfTest(string marker) =>
        new(SpoolRecordKind.SelfTest, LogRecord: null, marker);
}
