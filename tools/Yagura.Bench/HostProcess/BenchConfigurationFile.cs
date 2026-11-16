using System.Text.Json;
using System.Text.Json.Nodes;

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

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// <paramref name="configuration"/> のうち指定された項目だけを 1 つの設定ファイルへまとめて書く。
    /// 指定項目が 1 つもなければ何も書かない（既存の設定ファイルを上書きせず、ゼロ設定で起動させる）。
    /// </summary>
    /// <returns>設定ファイルを書いた場合 <c>true</c>。</returns>
    public static bool Write(string dataRoot, BenchHostConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.IsEmpty)
        {
            return false;
        }

        // 値はすべて文字列で書く。YaguraConfigurationOptions は設定値を string? で受け、解析と
        // 検証をローダが担うため、数値・真偽値のまま書くと読み込み時に型不一致で落ち、ホストが
        // 起動できない（BenchConfigurationFileTests が読み戻しで固定している）。
        var root = new JsonObject();

        if (!string.IsNullOrWhiteSpace(configuration.SqlServerConnectionString))
        {
            root["Storage"] = new JsonObject
            {
                ["Provider"] = "sqlserver",
                ["SqlServer"] = new JsonObject { ["ConnectionString"] = configuration.SqlServerConnectionString },
            };
        }

        if (configuration.SpoolQuotaBytes is { } quotaBytes)
        {
            root["Spool"] = new JsonObject { ["QuotaBytes"] = ToInvariant(quotaBytes) };
        }

        var ingestion = new JsonObject();

        if (configuration.UdpReceiveBufferBytes is { } receiveBufferBytes)
        {
            ingestion["Udp"] = new JsonObject { ["ReceiveBufferBytes"] = ToInvariant(receiveBufferBytes) };
        }

        var flowControl = new JsonObject();
        if (configuration.FlowControlEnabled is { } enabled)
        {
            flowControl["Enabled"] = enabled ? "true" : "false";
        }

        if (configuration.FlowControlMessagesPerSecond is { } messagesPerSecond)
        {
            flowControl["MessagesPerSecond"] = ToInvariant(messagesPerSecond);
        }

        if (configuration.FlowControlBurstSize is { } burstSize)
        {
            flowControl["BurstSize"] = ToInvariant(burstSize);
        }

        if (flowControl.Count > 0)
        {
            ingestion["FlowControl"] = flowControl;
        }

        if (ingestion.Count > 0)
        {
            root["Ingestion"] = ingestion;
        }

        File.WriteAllText(Path.Combine(dataRoot, FileName), root.ToJsonString(IndentedJsonOptions));
        return true;
    }

    /// <summary>
    /// SQL Server provider を使う設定ファイルを書く（database.md §1 参照。接続文字列は
    /// シナリオランナーの CLI 引数から受け取る）。
    /// </summary>
    public static void WriteSqlServerConfiguration(string dataRoot, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Write(dataRoot, new BenchHostConfiguration(SqlServerConnectionString: connectionString));
    }

    /// <summary>
    /// スプール容量を明示的に小さく絞った設定ファイルを書く（スプール発動シナリオ用。
    /// architecture.md §3.1「スプール容量」——上限到達を短時間の負荷で再現するため、
    /// 既定 1 GiB（SpoolConstants.DefaultQuotaBytes）ではなくベンチ向けの小さい値を使う）。
    /// </summary>
    public static void WriteSpoolQuotaConfiguration(string dataRoot, long quotaBytes) =>
        Write(dataRoot, new BenchHostConfiguration(SpoolQuotaBytes: quotaBytes));

    /// <summary>
    /// UDP 受信バッファサイズ（<c>Ingestion:Udp:ReceiveBufferBytes</c>。M-2）を明示指定した
    /// 設定ファイルを書く（バッファ値別の破棄ゼロ上限比較用）。
    /// </summary>
    public static void WriteUdpReceiveBufferConfiguration(string dataRoot, int receiveBufferBytes) =>
        Write(dataRoot, new BenchHostConfiguration(UdpReceiveBufferBytes: receiveBufferBytes));

    private static string ToInvariant(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 管理リスナのリモートバインド + アプリ独自認証 + HTTPS を有効化した設定ファイルを書く
    /// （ADR-0010 Phase 2 決定 8 受け入れ条件 (iv)「認証処理とログ受信の分離」の負荷試験用。
    /// Windows 統合認証（AD 依存）ではなくアプリ独自認証を使う——ベンチ実行環境が AD を
    /// 持たないことを前提にしない）。
    /// </summary>
    public static void WriteAdminRemoteHttpsAuthLoadConfiguration(string dataRoot, string certificateThumbprint, int adminHttpsPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateThumbprint);

        var json = JsonSerializer.Serialize(new
        {
            Admin = new
            {
                RemoteBinding = new { Enabled = "true" },
                Authentication = new { App = new { Enabled = "true" } },
                Https = new
                {
                    Enabled = "true",
                    CertificateThumbprint = certificateThumbprint,
                    Port = adminHttpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            },
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(Path.Combine(dataRoot, FileName), json);
    }
}
