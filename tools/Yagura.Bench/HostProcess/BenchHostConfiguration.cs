namespace Yagura.Bench.HostProcess;

/// <summary>
/// ベンチがホスト起動前に <c>yagura.json</c> へ書く設定の集合。<c>null</c> の項目は書かず、
/// ホストの既定値（または既存の設定ファイルの値）に委ねる。
/// </summary>
/// <remarks>
/// 設定ファイルは 1 つしかないため、項目ごとにファイルを書くと後の書き込みが前の書き込みを消す。
/// 組み合わせ（SQL Server + 流量制御無効 + 受信バッファ指定など）はこの型にまとめてから
/// <see cref="BenchConfigurationFile.Write(string, BenchHostConfiguration)"/> で 1 回だけ書く。
/// </remarks>
/// <param name="SqlServerConnectionString">指定時は SQL Server provider を使う（<c>Storage:Provider</c> = sqlserver）。</param>
/// <param name="SpoolQuotaBytes">スプールのディスク使用量上限（<c>Spool:QuotaBytes</c>）。</param>
/// <param name="UdpReceiveBufferBytes">UDP 受信バッファ（<c>Ingestion:Udp:ReceiveBufferBytes</c>）。</param>
/// <param name="FlowControlEnabled">流量制御の有効/無効（<c>Ingestion:FlowControl:Enabled</c>）。</param>
/// <param name="FlowControlMessagesPerSecond">流量制御の持続速度（<c>Ingestion:FlowControl:MessagesPerSecond</c>）。</param>
/// <param name="FlowControlBurstSize">流量制御のバーストサイズ（<c>Ingestion:FlowControl:BurstSize</c>）。</param>
public sealed record BenchHostConfiguration(
    string? SqlServerConnectionString = null,
    long? SpoolQuotaBytes = null,
    int? UdpReceiveBufferBytes = null,
    bool? FlowControlEnabled = null,
    int? FlowControlMessagesPerSecond = null,
    int? FlowControlBurstSize = null)
{
    /// <summary>書くべき項目が 1 つもないか（このときは設定ファイルを書かない）。</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(SqlServerConnectionString) &&
        SpoolQuotaBytes is null &&
        UdpReceiveBufferBytes is null &&
        FlowControlEnabled is null &&
        FlowControlMessagesPerSecond is null &&
        FlowControlBurstSize is null;
}
