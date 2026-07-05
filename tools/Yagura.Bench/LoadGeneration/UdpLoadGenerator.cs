using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Yagura.Bench.LoadGeneration;

/// <summary>
/// UDP 負荷生成器（Issue #60）。持続流量・バーストの両モードに対応する。
/// </summary>
/// <remarks>
/// <para>
/// <b>送信数を正確に把握する</b>: 既製ツール（例: 単純なワンライナースクリプト）は送出成功数の
/// 保証が取れないため不採用（Issue #60 の明示要求）。本実装は各 <c>SendAsync</c> 呼び出しを
/// <c>await</c> し、成功・失敗を <see cref="Interlocked"/> でカウントする——fire-and-forget にしない。
/// </para>
/// <para>
/// <b>送信側のボトルネック回避</b>: 1 つの <see cref="UdpClient"/>（1 ソケット）に送出を集約すると、
/// 送信側の同期・バッファ管理がボトルネックになり得るため、
/// <see cref="LoadGeneratorOptions.SenderSocketCount"/> 個のソケットに送出を分散する
/// （§5.1「ベンチの負荷生成はベンチ自身がボトルネックにならない設計」）。各ソケットは専用の
/// <see cref="Task"/> 上で動作し、連番はソケット横断で単調増加する共有カウンタから払い出す
/// （どのソケットが送っても連番の欠落なく検証器が追跡できるようにするため）。
/// </para>
/// </remarks>
public sealed class UdpLoadGenerator
{
    /// <summary>
    /// 負荷を送出する。
    /// </summary>
    public static async Task<LoadGeneratorResult> RunAsync(LoadGeneratorOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Transport != LoadTransport.Udp)
        {
            throw new ArgumentException("UdpLoadGenerator は Transport.Udp 専用。", nameof(options));
        }

        var endpoint = new IPEndPoint(IPAddress.Parse(options.TargetHost), options.TargetPort);
        var padding = options.PaddingBytes > 0 ? new string('x', options.PaddingBytes) : string.Empty;

        long succeeded = 0;
        long failed = 0;
        long nextSequence = -1; // Interlocked.Increment で 0 始まりにするため -1 から開始

        var stopwatch = Stopwatch.StartNew();

        var senderTasks = new Task[options.SenderSocketCount];
        for (var socketIndex = 0; socketIndex < options.SenderSocketCount; socketIndex++)
        {
            senderTasks[socketIndex] = Task.Run(async () =>
            {
                using var client = new UdpClient();

                if (options.Pattern == LoadPattern.Burst)
                {
                    await RunBurstOnSocketAsync(client, endpoint, options, padding, () => Interlocked.Increment(ref nextSequence), IncrementSucceeded, IncrementFailed, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RunSustainedOnSocketAsync(client, endpoint, options, padding, () => Interlocked.Increment(ref nextSequence), IncrementSucceeded, IncrementFailed, socketIndex, cancellationToken)
                        .ConfigureAwait(false);
                }
            }, cancellationToken);
        }

        await Task.WhenAll(senderTasks).ConfigureAwait(false);
        stopwatch.Stop();

        var attempted = succeeded + failed;
        return new LoadGeneratorResult(options.RunId, attempted, succeeded, failed, stopwatch.Elapsed, options.SenderSocketCount);

        void IncrementSucceeded() => Interlocked.Increment(ref succeeded);
        void IncrementFailed() => Interlocked.Increment(ref failed);
    }

    /// <summary>
    /// バーストモード: 1 ソケットが割り当てられた総数分を「可能な限り高速」に送出する
    /// （送出間隔を空けない。<see cref="LoadGeneratorOptions.BurstCount"/> をソケット数で均等分割）。
    /// </summary>
    private static async Task RunBurstOnSocketAsync(
        UdpClient client,
        IPEndPoint endpoint,
        LoadGeneratorOptions options,
        string padding,
        Func<long> nextSequence,
        Action onSucceeded,
        Action onFailed,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var sequence = nextSequence();
            if (sequence >= options.BurstCount)
            {
                return;
            }

            await SendOneAsync(client, endpoint, options.RunId, sequence, padding, onSucceeded, onFailed, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 持続流量モード: 全ソケット合計で毎秒 <see cref="LoadGeneratorOptions.RatePerSecond"/> 通を
    /// <see cref="LoadGeneratorOptions.DurationSeconds"/> 秒間維持する。1 ソケットあたりの目標間隔を
    /// 均等割りし、<see cref="PeriodicTimer"/> で駆動する（固定 <c>Task.Delay</c> ループの累積ドリフトを
    /// 避けるため、基準時刻から計算した目標時刻との差でスリープ時間を補正する）。
    /// </summary>
    private static async Task RunSustainedOnSocketAsync(
        UdpClient client,
        IPEndPoint endpoint,
        LoadGeneratorOptions options,
        string padding,
        Func<long> nextSequence,
        Action onSucceeded,
        Action onFailed,
        int socketIndex,
        CancellationToken cancellationToken)
    {
        var perSocketRate = (double)options.RatePerSecond / options.SenderSocketCount;
        if (perSocketRate <= 0)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(1.0 / perSocketRate);
        var runDuration = TimeSpan.FromSeconds(options.DurationSeconds);

        // 時間窓は 1 つの基準時刻から両端を構築する（conventions.md「UtcNow の複数回読取は
        // 微小なずれで不安定化するため禁止」）。
        var startedAt = DateTimeOffset.UtcNow;
        var deadline = startedAt + runDuration;
        var sentOnThisSocket = 0L;

        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= deadline || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var sequence = nextSequence();
            await SendOneAsync(client, endpoint, options.RunId, sequence, padding, onSucceeded, onFailed, cancellationToken).ConfigureAwait(false);
            sentOnThisSocket++;

            // 目標時刻（開始 + 送出済み件数 * 間隔）との差分だけ待つ——固定間隔の積み上げでは
            // 送出自体にかかった時間分が毎回ドリフトとして蓄積するため、絶対時刻基準で補正する。
            var targetNext = startedAt + (interval * sentOnThisSocket);
            var delay = targetNext - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static async Task SendOneAsync(
        UdpClient client,
        IPEndPoint endpoint,
        string runId,
        long sequence,
        string padding,
        Action onSucceeded,
        Action onFailed,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = LoadGeneration.BenchMessageFactory.BuildMessage(runId, sequence, DateTimeOffset.UtcNow, padding);
            await client.SendAsync(payload, endpoint, cancellationToken).ConfigureAwait(false);
            onSucceeded();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // 送信側ソケットバッファ溢れ等——送出失敗として計上する（fire-and-forget にせず
            // 必ず成功・失敗のいずれかに分類する。Issue #60「送信数を正確に把握できる」）。
            onFailed();
        }
    }
}
