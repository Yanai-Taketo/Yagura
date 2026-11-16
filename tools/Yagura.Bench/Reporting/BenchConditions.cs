using Yagura.Bench.Scenarios;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;

namespace Yagura.Bench.Reporting;

/// <summary>
/// 1 回の実行が「どの構成のホストで測ったか」の記録（結果 JSON の <c>Conditions</c>）。
/// </summary>
/// <remarks>
/// 同じシナリオ・同じレートでも、流量制御の有無や受信バッファで結果の意味が変わる。集計
/// （tools/Yagura.Bench/scripts/aggregate-nominal-survey.py）はこの記録で、条件を外れた標本を
/// 見分ける。値はベンチが渡した設定から導く期待値であり、ホスト内部の実値を読み戻したものではない
/// （<c>--data-root</c> に既存の設定ファイルがある場合、ベンチが上書きしない項目はそちらが効く）。
/// </remarks>
/// <param name="Provider">保存先（<c>Sqlite</c> / <c>SqlServer</c>）。</param>
/// <param name="FlowControlEnabled">流量制御が有効な構成か。</param>
/// <param name="FlowControlMessagesPerSecond">流量制御の持続速度（件/秒）。無効時は <c>null</c>。</param>
/// <param name="FlowControlBurstSize">流量制御のバーストサイズ（件）。無効時は <c>null</c>。</param>
/// <param name="FlowControlOverridden">流量制御の項目をベンチが設定ファイルへ書いたか（<c>false</c> = 製品既定）。</param>
/// <param name="UdpReceiveBufferBytes">UDP 受信バッファ（バイト）。</param>
/// <param name="UdpReceiveBufferOverridden">受信バッファをベンチが設定ファイルへ書いたか（<c>false</c> = 製品既定）。</param>
/// <param name="DrainTimeoutSeconds">送出後に drain 完了を待った上限（秒）。</param>
/// <param name="DrainCompleted">
/// 上限内に drain が完了したか。<c>false</c> の実行は drain 途中の件数で突合しており、
/// 保存件数・導出値の解釈に注意が要る。
/// </param>
/// <param name="DrainWaitElapsed">drain 完了の待ちに要した時間。</param>
/// <param name="GracefulStopSucceeded">
/// ホストをグレースフル停止できたか（<c>false</c> = Kill へフォールバック）。Kill では直近の定期
/// 永続化以降のカウンタ増分が失われ、退避や破棄が 0 に見えることがあるため、合否の判定では
/// <c>false</c> の段を信用しない。
/// </param>
public sealed record BenchConditions(
    string Provider,
    bool FlowControlEnabled,
    int? FlowControlMessagesPerSecond,
    int? FlowControlBurstSize,
    bool FlowControlOverridden,
    int UdpReceiveBufferBytes,
    bool UdpReceiveBufferOverridden,
    int DrainTimeoutSeconds,
    bool DrainCompleted,
    TimeSpan DrainWaitElapsed,
    bool? GracefulStopSucceeded = null)
{
    /// <summary>
    /// CLI 指定と drain 待ちの観測から測定条件を組み立てる。ホストへ渡す設定の規則は
    /// <see cref="ScenarioRunner.BuildHostConfiguration"/> と同じものを使う。
    /// </summary>
    public static BenchConditions From(
        ScenarioOptions options,
        bool drainCompleted,
        TimeSpan drainWaitElapsed,
        bool? gracefulStopSucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var host = ScenarioRunner.BuildHostConfiguration(options);

        // 閾値だけを指定した場合も製品既定（有効）が残るため、有効として記録する。
        var flowControlEnabled = host.FlowControlEnabled ?? true;
        var flowControlOverridden =
            host.FlowControlEnabled is not null ||
            host.FlowControlMessagesPerSecond is not null ||
            host.FlowControlBurstSize is not null;

        return new BenchConditions(
            Provider: string.IsNullOrWhiteSpace(host.SqlServerConnectionString) ? "Sqlite" : "SqlServer",
            FlowControlEnabled: flowControlEnabled,
            FlowControlMessagesPerSecond: flowControlEnabled
                ? host.FlowControlMessagesPerSecond ?? TokenBucketIngressGate.DefaultMessagesPerSecond
                : null,
            FlowControlBurstSize: flowControlEnabled
                ? host.FlowControlBurstSize ?? TokenBucketIngressGate.DefaultBurstSize
                : null,
            FlowControlOverridden: flowControlOverridden,
            UdpReceiveBufferBytes: host.UdpReceiveBufferBytes ?? UdpSyslogListenerOptions.DefaultReceiveBufferBytes,
            UdpReceiveBufferOverridden: host.UdpReceiveBufferBytes is not null,
            DrainTimeoutSeconds: options.DrainTimeoutSeconds,
            DrainCompleted: drainCompleted,
            DrainWaitElapsed: drainWaitElapsed,
            GracefulStopSucceeded: gracefulStopSucceeded);
    }
}
