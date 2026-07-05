using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Yagura.Bench.HostProcess;

/// <summary>
/// Yagura.Host を子プロセスとして起動・停止するランチャ（Issue #60。§5.1「構成: 負荷生成器 →
/// 本体 → 検証器」の「本体」に相当）。
/// </summary>
/// <remarks>
/// tests/Yagura.E2E.Tests の起動パターン（<c>dotnet Yagura.Host.dll</c> + 環境変数によるゼロ設定
/// 上書き + stdout からのポート取得）をそのまま踏襲する——実バイナリでの実測が本ベンチの目的であり、
/// 単体テスト用のインメモリ結線ではなく実プロセスを対象にする必要があるため。
/// </remarks>
public sealed class BenchHostProcess : IAsyncDisposable
{
    private const string UdpListenerLogPrefix = "UDP syslog listener started on port";
    private const string TcpListenerLogPrefix = "TCP syslog listener started on port";
    private static readonly Regex ViewerListeningPortPattern =
        new(@"Now listening on:\s*http://\[::\]:(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex ViewerListeningLoopbackPortPattern =
        new(@"Now listening on:\s*http://(?:127\.0\.0\.1|\[::1\]):(\d+)\s*$", RegexOptions.Compiled);

    private readonly Process _process;
    private readonly List<string> _stdoutLines = [];
    private readonly object _stdoutLock = new();

    private BenchHostProcess(Process process)
    {
        _process = process;
    }

    public int UdpPort { get; private set; }

    public int TcpPort { get; private set; }

    public int ViewerHttpPort { get; private set; }

    /// <summary>子プロセスの標準出力全行（デバッグ・障害調査用に保持）。</summary>
    public IReadOnlyList<string> StdoutLines
    {
        get
        {
            lock (_stdoutLock)
            {
                return _stdoutLines.ToArray();
            }
        }
    }

    /// <summary>
    /// Yagura.Host.dll を子プロセスとして起動し、UDP/TCP/閲覧リスナの起動ログを待って
    /// 実バインドポートを取得する。
    /// </summary>
    /// <param name="dataRoot">データルート（一時ディレクトリを推奨。呼び出し側が生成・削除を管理する）。</param>
    /// <param name="udpPort">起動時 UDP ポート指定（既定 0 = OS 採番）。</param>
    /// <param name="tcpPort">起動時 TCP ポート指定（既定 0 = OS 採番）。</param>
    /// <param name="httpPort">起動時閲覧 HTTP ポート指定（既定 0 = OS 採番）。</param>
    /// <param name="adminPort">起動時管理 HTTP ポート指定（既定 0 = OS 採番）。</param>
    /// <param name="startupTimeout">起動ログ待機のタイムアウト（既定 30 秒。E2E テストと同じ既定値）。</param>
    /// <summary>
    /// 子プロセス起動に使う dotnet 実行ファイルのパスを解決する。
    /// </summary>
    /// <remarks>
    /// 固定文字列 <c>"dotnet"</c>（PATH 依存）では、dotnet が PATH に登録されていない環境からの
    /// CLI 実行が <c>Win32Exception</c>（ファイルが見つからない）で失敗する（本開発機で実際に
    /// 発生——`dotnet test` 経由の VSTest ホスト下では PATH が通っており顕在化しなかった）。
    /// 解決順序: (1) <c>DOTNET_HOST_PATH</c> 環境変数（dotnet SDK/ホストが子コンテキストへ
    /// 設定する公式変数）、(2) 自プロセスが dotnet ホストで実行されている場合はその実体
    /// （<see cref="Environment.ProcessPath"/>）、(3) 素の <c>"dotnet"</c>（PATH 解決に委ねる）。
    /// </remarks>
    private static string ResolveDotnetExecutablePath()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var fileName = Path.GetFileNameWithoutExtension(processPath);
            if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }

            // apphost（Yagura.Bench.exe 等）で実行されている場合、同じランタイムを持つ
            // dotnet.exe はランタイムディレクトリの祖先（<dotnet root>）に居る。
            // RuntimeEnvironment のランタイムパス（<dotnet root>/shared/<fw>/<ver>/）から遡る。
            var runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
            var candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }

    public static async Task<BenchHostProcess> StartAsync(
        string dataRoot,
        int udpPort = 0,
        int tcpPort = 0,
        int httpPort = 0,
        int adminPort = 0,
        TimeSpan? startupTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Directory.CreateDirectory(dataRoot);

        var hostDllPath = ResolveYaguraHostDllPath();
        if (!File.Exists(hostDllPath))
        {
            throw new FileNotFoundException($"Yagura.Host.dll が見つからない: {hostDllPath}", hostDllPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetExecutablePath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(hostDllPath);

        startInfo.Environment["YAGURA_DATAROOT"] = dataRoot;
        startInfo.Environment["YAGURA_HTTP_PORT"] = httpPort.ToString();
        startInfo.Environment["YAGURA_UDP_PORT"] = udpPort.ToString();
        startInfo.Environment["YAGURA_TCP_PORT"] = tcpPort.ToString();
        startInfo.Environment["YAGURA_ADMIN_PORT"] = adminPort.ToString();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var bench = new BenchHostProcess(process);

        var udpPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcpPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (bench._stdoutLock)
            {
                bench._stdoutLines.Add(e.Data);
            }

            var udpMatch = Regex.Match(e.Data, $@"{Regex.Escape(UdpListenerLogPrefix)}\s+(\d+)");
            if (udpMatch.Success && int.TryParse(udpMatch.Groups[1].Value, out var parsedUdpPort))
            {
                udpPortTcs.TrySetResult(parsedUdpPort);
            }

            var tcpMatch = Regex.Match(e.Data, $@"{Regex.Escape(TcpListenerLogPrefix)}\s+(\d+)");
            if (tcpMatch.Success && int.TryParse(tcpMatch.Groups[1].Value, out var parsedTcpPort))
            {
                tcpPortTcs.TrySetResult(parsedTcpPort);
            }

            // 閲覧リスナは既定(Lan)で全インターフェース bind のため "http://[::]:{port}"、
            // 明示的に loopback 限定にした場合は "127.0.0.1:"/"[::1]:" 表記になる
            // （tests/Yagura.E2E.Tests/ZeroConfigFirstRunE2ETests.cs のコメント参照）。両方を見る。
            var viewerMatch = ViewerListeningPortPattern.Match(e.Data);
            if (!viewerMatch.Success)
            {
                viewerMatch = ViewerListeningLoopbackPortPattern.Match(e.Data);
            }

            if (viewerMatch.Success && int.TryParse(viewerMatch.Groups[1].Value, out var parsedViewerPort))
            {
                viewerPortTcs.TrySetResult(parsedViewerPort);
            }
        };

        // stderr も採取する(接頭辞で stdout と区別)。オーナー実機での SQL Server 実測時、
        // 起動タイムアウトの原因(provider 初期化の失敗)が子プロセスの出力ごと闇に消えて
        // 診断不能になった実障害への対処——起動失敗の一次情報は必ず拾う。
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (bench._stdoutLock)
            {
                bench._stdoutLines.Add($"[stderr] {e.Data}");
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeout = startupTimeout ?? TimeSpan.FromSeconds(30);
        try
        {
            bench.UdpPort = await WaitWithTimeoutAsync(udpPortTcs.Task, timeout, "UDP リスナ起動ログ").ConfigureAwait(false);
            bench.TcpPort = await WaitWithTimeoutAsync(tcpPortTcs.Task, timeout, "TCP リスナ起動ログ").ConfigureAwait(false);
            bench.ViewerHttpPort = await WaitWithTimeoutAsync(viewerPortTcs.Task, timeout, "閲覧リスナ HTTP listening ログ").ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // 起動待機のタイムアウトは「子プロセス側で何かが起きた」の症状にすぎない。
            // 一次情報(子プロセスの stdout/stderr の末尾・終了コード)を例外に含めて、
            // 実機での診断を一目で可能にする(オーナー実機の SQL Server 初期化失敗が
            // 診断不能だった実障害への対処)。プロセスは取り残さず必ず止める。
            var exitNote = process.HasExited
                ? $"子プロセスは既に終了している(exit code {process.ExitCode})。"
                : "子プロセスはまだ実行中(このあと Kill する)。";

            string[] outputTail;
            lock (bench._stdoutLock)
            {
                outputTail = bench._stdoutLines.TakeLast(40).ToArray();
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new TimeoutException(
                $"{ex.Message}\n{exitNote}\n--- 子プロセス出力(末尾 {outputTail.Length} 行) ---\n{string.Join("\n", outputTail)}",
                ex);
        }

        return bench;
    }

    /// <summary>
    /// グレースフル停止（<see cref="ConsoleCtrlSender"/> で Ctrl+C を送出し、.NET Generic Host の
    /// 通常の停止シーケンス——<see cref="Yagura.Host.IngestionHostedService.StopAsync"/> による
    /// architecture.md §1.3 手順 1〜3（メタデータ領域への最終カウンタ書き込みを含む）——を
    /// 実行させる）。<see cref="ConsoleCtrlSender"/> のコメント参照: 本ベンチの検証器は
    /// 停止手順 3 の最終カウンタ書き込みに依存するため、tests/Yagura.E2E.Tests が採用した
    /// 単純な <c>Kill</c> では実機検証で突合が不成立になった経緯がある。
    /// Ctrl+C 送出が失敗した場合、または停止がタイムアウトした場合のみ <c>Kill</c> へ
    /// フォールバックする（フォールバック時は正常停止手順を経ないため、呼び出し側が
    /// 十分な静定時間を置いていたとしても、直近の定期永続化以降の増分は失われ得る——
    /// <see cref="GracefulStopSucceeded"/> で呼び出し側がフォールバックの発生を検知できる）。
    /// </summary>
    public async Task StopGracefullyAsync(TimeSpan? timeout = null)
    {
        if (_process.HasExited)
        {
            return;
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(20);
        var ctrlCSent = TrySendCtrlCViaHelper(_process.Id);

        if (ctrlCSent)
        {
            var exitedGracefully = await WaitForExitAsync(effectiveTimeout).ConfigureAwait(false);
            if (exitedGracefully)
            {
                GracefulStopSucceeded = true;
                return;
            }
        }

        // Ctrl+C 送出自体が失敗した、またはグレースフル停止がタイムアウトした場合の
        // フォールバック（架構上「正常停止できないなら Kill でよい」という許容——ただし
        // 本ベンチの突合精度はこの経路では低下し得ることを記録する）。
        GracefulStopSucceeded = false;
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        var killedExited = await WaitForExitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (!killedExited)
        {
            throw new TimeoutException("Yagura.Host 子プロセスの停止がタイムアウトした（Kill フォールバック後も終了しなかった）。");
        }
    }

    /// <summary>
    /// 直近の <see cref="StopGracefullyAsync"/> がグレースフル停止（Ctrl+C 経由）で完了したか。
    /// <c>false</c> は Kill フォールバックが発生したことを示す（検証器が突合結果の解釈に使う）。
    /// </summary>
    public bool? GracefulStopSucceeded { get; private set; }

    /// <summary>
    /// Ctrl+C 送出を使い捨てのヘルパープロセス（自分自身の dll を <c>__send-ctrlc</c> モードで
    /// 起動）に隔離して実行する。
    /// </summary>
    /// <remarks>
    /// 送出処理（<see cref="ConsoleCtrlSender"/>）は <c>FreeConsole</c> で呼び出しプロセスの
    /// コンソールを失う。これをベンチ本体プロセスで行うと、実コンソールからの対話実行時に
    /// 以後の <c>Console.WriteLine</c> が未処理例外でクラッシュする実障害が起きた
    /// （exit 0xE0434352。オーナー実機 + ローカル再現で確認。再アタッチによる修復は環境ごとに
    /// 副作用が異なり安定しなかった）。使い捨てプロセスに隔離すれば本体のコンソール状態には
    /// 一切影響しない。ヘルパーの exit code 0 = 送出成功（<see cref="ConsoleCtrlSender.TrySendCtrlC"/>
    /// の戻り値）。
    /// </remarks>
    private static bool TrySendCtrlCViaHelper(int targetProcessId)
    {
        var benchDllPath = Path.Combine(AppContext.BaseDirectory, "Yagura.Bench.dll");
        if (!File.Exists(benchDllPath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetExecutablePath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(benchDllPath);
        startInfo.ArgumentList.Add("__send-ctrlc");
        startInfo.ArgumentList.Add(targetProcessId.ToString());

        try
        {
            using var helper = Process.Start(startInfo);
            if (helper is null)
            {
                return false;
            }

            if (!helper.WaitForExit(10_000))
            {
                helper.Kill(entireProcessTree: true);
                return false;
            }

            return helper.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // ヘルパーの起動自体に失敗した場合は Kill フォールバックに委ねる。
            return false;
        }
    }

    private async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        return _process.HasExited;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // 既に終了している場合等。
            }
        }

        _process.Dispose();
        await Task.CompletedTask;
    }

    private static string ResolveYaguraHostDllPath() =>
        Path.Combine(AppContext.BaseDirectory, "Yagura.Host.dll");

    private static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, string description)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException($"{description} の待機がタイムアウトした（{timeout}）。");
        }

        return await task.ConfigureAwait(false);
    }
}
