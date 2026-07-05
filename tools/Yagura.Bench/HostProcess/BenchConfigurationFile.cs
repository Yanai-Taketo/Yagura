using System.Text.Json;

namespace Yagura.Bench.HostProcess;

/// <summary>
/// ベンチが Yagura.Host 起動前にデータルートへ書く <c>yagura.json</c> 設定ファイルの組み立て
/// （Issue #60。src/Yagura.Host/Configuration/YaguraConfigurationLoader.cs の KnownKeys に
/// 対応するキーのみ使用する）。
/// </summary>
/// <remarks>
/// 環境変数（<c>YAGURA_DATAROOT</c>/<c>YAGURA_HTTP_PORT</c>/<c>YAGURA_UDP_PORT</c>/
/// <c>YAGURA_TCP_PORT</c>/<c>YAGURA_ADMIN_PORT</c>）で上書きできない設定（provider 選択・
/// SQL Server 接続文字列・スプール容量等）は設定ファイル経由で渡す必要がある
/// （YaguraConfigurationLoader の優先順位「環境変数 &gt; 設定ファイル &gt; 既定値」のとおり、
/// 環境変数が存在しないキーは設定ファイルでのみ指定できる）。
/// </remarks>
public static class BenchConfigurationFile
{
    public const string FileName = "yagura.json";

    /// <summary>
    /// SQL Server provider を使う設定ファイルを書く（database.md §1 参照。接続文字列は
    /// シナリオランナーの CLI 引数から受け取る）。
    /// </summary>
    public static void WriteSqlServerConfiguration(string dataRoot, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var json = JsonSerializer.Serialize(new
        {
            Storage = new
            {
                Provider = "sqlserver",
                SqlServer = new { ConnectionString = connectionString },
            },
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(Path.Combine(dataRoot, FileName), json);
    }

    /// <summary>
    /// スプール容量を明示的に小さく絞った設定ファイルを書く（スプール発動シナリオ用。
    /// architecture.md §3.1「スプール容量」——上限到達を短時間の負荷で再現するため、
    /// 既定 1 GiB（SpoolConstants.DefaultQuotaBytes）ではなくベンチ向けの小さい値を使う）。
    /// </summary>
    public static void WriteSpoolQuotaConfiguration(string dataRoot, long quotaBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var json = JsonSerializer.Serialize(new
        {
            Spool = new { QuotaBytes = quotaBytes },
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(Path.Combine(dataRoot, FileName), json);
    }

    /// <summary>
    /// UDP 受信バッファサイズ（<c>Ingestion:Udp:ReceiveBufferBytes</c>。M-2）を明示指定した
    /// 設定ファイルを書く（バッファ値別の破棄ゼロ上限比較用。ScenarioRunner の
    /// SustainedZeroDrop/BurstQ1Drop から呼ばれる）。
    /// </summary>
    public static void WriteUdpReceiveBufferConfiguration(string dataRoot, int receiveBufferBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var json = JsonSerializer.Serialize(new
        {
            Ingestion = new
            {
                Udp = new { ReceiveBufferBytes = receiveBufferBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            },
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(Path.Combine(dataRoot, FileName), json);
    }
}
