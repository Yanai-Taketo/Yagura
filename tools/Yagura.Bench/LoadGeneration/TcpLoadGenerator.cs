using System.Diagnostics;
using System.Net.Sockets;

namespace Yagura.Bench.LoadGeneration;

/// <summary>
/// TCP 負荷生成器（Issue #60）。octet-counting framing（RFC 6587 §3.4.1）で送出する。
/// </summary>
/// <remarks>
/// <para>
/// UDP と異なり TCP は接続単位のストリームであり、<see cref="LoadGeneratorOptions.SenderSocketCount"/>
/// は「並行接続数」に対応する。各接続は起動時に確立し、送出完了まで維持する
/// （接続確立コストを送出スループット計測に含めないため、事前に全接続を確立してから送出を開始する）。
/// </para>
/// <para>
/// <b>送信数の正確な把握</b>: <see cref="NetworkStream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>
/// を <c>await</c> し、例外なく完了した時点で成功と計上する。TCP の場合、書き込みの完了は
/// 「送信バッファへ渡った」ことを意味し「受信側に届いた」ことまでは保証しないが、これは
/// TCP プロトコル自体の性質であり（architecture.md §4.5「転送自体は信頼できる」）、送出側が
/// 主張できる最大限の確実性である。読み取り停止中の送信元（サーバ Q1 満杯時。§3.1）は
/// <c>WriteAsync</c> 自体がブロックし得るため、バーストシナリオでの「Q1 破棄の発生有無」検証は
/// UDP 側で行う（TCP は破棄ではなく読み取り停止で表れる設計のため。§3.1 TCP 行）。
/// </para>
/// </remarks>
public sealed class TcpLoadGenerator
{
    public static async Task<LoadGeneratorResult> RunAsync(LoadGeneratorOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Transport != LoadTransport.Tcp)
        {
            throw new ArgumentException("TcpLoadGenerator は Transport.Tcp 専用。", nameof(options));
        }

        var padding = options.PaddingBytes > 0 ? new string('x', options.PaddingBytes) : string.Empty;

        // 全接続を先に確立してから送出を開始する（接続確立コストをスループット計測から除く）。
        var clients = new TcpClient[options.SenderSocketCount];
        var streams = new NetworkStream[options.SenderSocketCount];
        try
        {
            for (var i = 0; i < options.SenderSocketCount; i++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(options.TargetHost, options.TargetPort, cancellationToken).ConfigureAwait(false);
                clients[i] = client;
                streams[i] = client.GetStream();
            }

            long succeeded = 0;
            long failed = 0;
            long nextSequence = -1;

            var stopwatch = Stopwatch.StartNew();

            var senderTasks = new Task[options.SenderSocketCount];
            for (var socketIndex = 0; socketIndex < options.SenderSocketCount; socketIndex++)
            {
                var stream = streams[socketIndex];
                senderTasks[socketIndex] = Task.Run(async () =>
                {
                    if (options.Pattern == LoadPattern.Burst)
                    {
                        await RunBurstOnStreamAsync(stream, options, padding, () => Interlocked.Increment(ref nextSequence), IncrementSucceeded, IncrementFailed, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await RunSustainedOnStreamAsync(stream, options, padding, () => Interlocked.Increment(ref nextSequence), IncrementSucceeded, IncrementFailed, cancellationToken)
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
        finally
        {
            foreach (var client in clients)
            {
                client?.Dispose();
            }
        }
    }

    private static async Task RunBurstOnStreamAsync(
        NetworkStream stream,
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

            await SendOneAsync(stream, options.RunId, sequence, padding, onSucceeded, onFailed, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RunSustainedOnStreamAsync(
        NetworkStream stream,
        LoadGeneratorOptions options,
        string padding,
        Func<long> nextSequence,
        Action onSucceeded,
        Action onFailed,
        CancellationToken cancellationToken)
    {
        var perSocketRate = (double)options.RatePerSecond / options.SenderSocketCount;
        if (perSocketRate <= 0)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(1.0 / perSocketRate);
        var runDuration = TimeSpan.FromSeconds(options.DurationSeconds);

        var startedAt = DateTimeOffset.UtcNow;
        var deadline = startedAt + runDuration;
        var sentOnThisStream = 0L;

        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= deadline || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var sequence = nextSequence();
            await SendOneAsync(stream, options.RunId, sequence, padding, onSucceeded, onFailed, cancellationToken).ConfigureAwait(false);
            sentOnThisStream++;

            var targetNext = startedAt + (interval * sentOnThisStream);
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
        NetworkStream stream,
        string runId,
        long sequence,
        string padding,
        Action onSucceeded,
        Action onFailed,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = BenchMessageFactory.BuildMessage(runId, sequence, DateTimeOffset.UtcNow, padding);
            var frame = BenchMessageFactory.WrapOctetCounting(message);
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            onSucceeded();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            onFailed();
        }
    }
}
