using System.Globalization;
using System.Text;

namespace Yagura.Bench.StorageBench;

/// <summary>
/// DB-9/DB-10 のストレージベンチマークが投入する合成ログレコードの組み立て
/// （tools/Yagura.Bench/README.md 参照）。受信パイプラインを経由しないため
/// <see cref="Yagura.Bench.LoadGeneration.BenchMessageFactory"/>（syslog ワイヤ形式）とは別系統——
/// 本ファクトリは <see cref="Yagura.Storage.ILogStore"/> へ直接書き込む列値そのものを返す。
/// </summary>
public static class SyntheticLogRecordFactory
{
    /// <summary>
    /// DB-6 保証集合の正例（database.md §1.2「Issue #146 の再現例（CAFÉ/café）」）。
    /// </summary>
    public const string NonAsciiNeedleStored = "CAFÉ";

    /// <summary><see cref="NonAsciiNeedleStored"/> を検索するときの検索語（大文字小文字のみ違う）。</summary>
    public const string NonAsciiNeedleSearch = "café";

    /// <summary>ASCII の一般的な検索語（中程度の選択性を想定した語）。</summary>
    public const string AsciiNeedleStored = "ERROR";

    /// <summary><see cref="AsciiNeedleStored"/> を検索するときの検索語（大文字小文字のみ違う）。</summary>
    public const string AsciiNeedleSearch = "error";

    private static readonly string[] HostPrefixes = ["web", "db", "fw", "sw", "app", "cache", "auth", "edge"];
    private static readonly string[] Services = ["nginx", "sshd", "systemd", "kernel", "cron", "dhcpd", "named", "sqlsrv"];
    private static readonly string[] Words =
    [
        "connection", "established", "timeout", "retry", "session", "closed", "handshake",
        "completed", "queue", "depth", "threshold", "exceeded", "buffer", "flush", "cache",
        "miss", "policy", "applied", "route", "updated", "interface", "link", "state", "changed",
    ];

    /// <summary>
    /// 1 行分の列値。<see cref="Yagura.Storage.LogRecord"/> と同じ意味の値だが、シーダーが
    /// provider ごとの生 INSERT へそのまま渡せるようプリミティブのタプルとして返す。
    /// </summary>
    public readonly record struct SyntheticRow(
        DateTimeOffset ReceivedAt,
        string SourceAddress,
        int SourcePort,
        int Protocol,
        int? Facility,
        int? Severity,
        string Hostname,
        string AppName,
        string ProcId,
        string Message,
        int ParseStatus);

    /// <summary>
    /// 指定した連番のレコードを生成する。<paramref name="asciiNeedleEveryNRows"/> の倍数の
    /// 行には <see cref="AsciiNeedleStored"/> を、<paramref name="nonAsciiNeedleRowIndexes"/> に
    /// 含まれる行には <see cref="NonAsciiNeedleStored"/> を Message へ埋め込む（DB-9 のクエリ
    /// レイテンシ計測が現実的な選択性を持つ検索語で測れるようにするため）。
    /// </summary>
    public static SyntheticRow Create(
        long index,
        DateTimeOffset baseline,
        int asciiNeedleEveryNRows = 100,
        IReadOnlySet<long>? nonAsciiNeedleRowIndexes = null)
    {
        // 決定的な疑似乱数——実行のたびに同じ行数指定で同じデータセットを再現できるようにする
        // （ベンチ結果の再現性。conventions.md「技術的主張の検証」の実体検証を再実行で追試可能にする）。
        var rand = new Random(unchecked((int)((ulong)index * 2654435761UL)));

        var host = $"{HostPrefixes[rand.Next(HostPrefixes.Length)]}-{index % 500:D3}";
        var service = Services[rand.Next(Services.Length)];
        var severity = rand.Next(0, 8);
        var facility = rand.Next(0, 24);

        var sb = new StringBuilder(160);
        var wordCount = 8 + rand.Next(12);
        for (var w = 0; w < wordCount; w++)
        {
            if (w > 0)
            {
                sb.Append(' ');
            }

            sb.Append(Words[rand.Next(Words.Length)]);
        }

        sb.Append(" seq=").Append(index.ToString(CultureInfo.InvariantCulture));

        if (asciiNeedleEveryNRows > 0 && index % asciiNeedleEveryNRows == 0)
        {
            // 大文字小文字が入り混じる形で埋め込む（ASCII 非区別規則の検証を兼ねる——
            // Issue #146 の根幹「error で ERROR/Error を拾えるか」）。
            sb.Append(rand.Next(2) == 0 ? " ERROR: disk threshold exceeded" : " Error: disk threshold exceeded");
        }

        if (nonAsciiNeedleRowIndexes is not null && nonAsciiNeedleRowIndexes.Contains(index))
        {
            sb.Append(" ").Append(NonAsciiNeedleStored).Append(" terminal restarted");
        }

        return new SyntheticRow(
            ReceivedAt: baseline.AddMilliseconds(index),
            SourceAddress: $"10.{(index / 65536) % 256}.{(index / 256) % 256}.{index % 256}",
            SourcePort: 514,
            Protocol: 0,
            Facility: facility,
            Severity: severity,
            Hostname: host,
            AppName: service,
            ProcId: (1000 + (index % 9000)).ToString(CultureInfo.InvariantCulture),
            Message: sb.ToString(),
            ParseStatus: 0);
    }
}
