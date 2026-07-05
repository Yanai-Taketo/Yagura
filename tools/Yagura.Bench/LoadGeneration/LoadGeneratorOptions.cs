namespace Yagura.Bench.LoadGeneration;

/// <summary>送出モード（Issue #60「持続流量」「バースト」の 2 モード）。</summary>
public enum LoadPattern
{
    /// <summary>持続流量: 毎秒 N 通を M 秒間送出する。</summary>
    Sustained,

    /// <summary>バースト: N 通を可能な限り高速で一斉送出する。</summary>
    Burst,
}

/// <summary>送出先のトランスポート。</summary>
public enum LoadTransport
{
    Udp,
    Tcp,
}

/// <summary>
/// 負荷生成器の構成（Issue #60）。
/// </summary>
/// <param name="Transport">送出先のトランスポート。</param>
/// <param name="Pattern">送出モード。</param>
/// <param name="TargetHost">送出先ホスト（既定 127.0.0.1。ベンチはループバック専有を前提とする。architecture.md §4.2）。</param>
/// <param name="TargetPort">送出先ポート。</param>
/// <param name="RunId">
/// ベンチ実行 ID（<see cref="BenchMessageFactory.BuildMessage"/> の RunId として埋め込む。
/// 同一データストアを使い回すシナリオ間で前回実行分と混同しないための識別子）。
/// </param>
/// <param name="RatePerSecond">
/// <see cref="LoadPattern.Sustained"/> 時の毎秒送出数。<see cref="LoadPattern.Burst"/> では無視する。
/// </param>
/// <param name="DurationSeconds">
/// <see cref="LoadPattern.Sustained"/> 時の送出継続秒数。<see cref="LoadPattern.Burst"/> では無視する。
/// </param>
/// <param name="BurstCount">
/// <see cref="LoadPattern.Burst"/> 時の総送出数。<see cref="LoadPattern.Sustained"/> では無視する。
/// </param>
/// <param name="SenderSocketCount">
/// 送出に使う並行ソケット数（送信側のボトルネック回避。§5.1 依頼「送信側の socket 数・
/// スレッド構成は報告に含める」）。1 ソケットの送出スループットが送出側で頭打ちにならないよう、
/// 複数ソケットに送出を分散する。TCP は 1 ソケット = 1 接続に対応する。
/// </param>
/// <param name="PaddingBytes">
/// メッセージ本文に追加するパディングの長さ（バイト。既定 0）。典型的な syslog メッセージ長を
/// 模すために使う——連番マーカーのみだと実運用より短いメッセージになるため。
/// </param>
public sealed record LoadGeneratorOptions(
    LoadTransport Transport,
    LoadPattern Pattern,
    string TargetHost,
    int TargetPort,
    string RunId,
    int RatePerSecond = 0,
    int DurationSeconds = 0,
    long BurstCount = 0,
    int SenderSocketCount = 4,
    int PaddingBytes = 0);
