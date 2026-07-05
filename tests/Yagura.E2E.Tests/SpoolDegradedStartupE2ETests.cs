using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Yagura.E2E.Tests;

/// <summary>
/// スプール領域を開けない状況（縮退運転。architecture.md §1.2）でも受信が止まらず、
/// 警告ログが出ることの E2E 確認。
/// </summary>
/// <remarks>
/// データルート配下の既定スプールディレクトリ（<c>spool</c>）の位置に、あらかじめ
/// 同名の通常ファイルを置いておくことで、<c>Directory.CreateDirectory</c> がそのパスを
/// ディレクトリとして扱えず失敗する状況を作る（ディスク障害・ACL 破損の代替模擬。
/// <c>Yagura.Storage.Tests.Spool.DiskSpoolOpenFailureTests</c> と同じ手法）。
/// </remarks>
public sealed class SpoolDegradedStartupE2ETests : IDisposable
{
    private const string UdpListenerLogPrefix = "UDP syslog listener started on port";
    // ロケール非依存の ASCII トークンで照合する(Program.cs の縮退警告に併記)。日本語本文での
    // 照合は、リダイレクトされた子プロセス stdout のコードページが日本語を表現できない環境
    // (en-US の GitHub Actions ランナー = CP437 等)で文字化けし、一致しない実障害があった。
    private const string SpoolDegradedLogMarker = "[spool-degraded-mode]";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-spool-degraded-{Guid.NewGuid():N}");
    private Process? _hostProcess;

    public void Dispose()
    {
        if (_hostProcess is { HasExited: false })
        {
            _hostProcess.Kill(entireProcessTree: true);
        }

        _hostProcess?.Dispose();

        if (Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // ベストエフォート（ZeroConfigFirstRunE2ETests と同じ判断）。
            }
        }
    }

    [Fact]
    public async Task SpoolDirectoryBlocked_HostStillReceives_AndLogsDegradedWarning()
    {
        Directory.CreateDirectory(_dataRoot);

        // 既定のスプールディレクトリ（データルート配下の "spool"）の位置に、あらかじめ
        // 同名の通常ファイルを置く——DiskSpool.TryOpen 内の Directory.CreateDirectory が
        // 失敗し、スプールなし縮退運転になるはず。
        var blockedSpoolPath = Path.Combine(_dataRoot, "spool");
        File.WriteAllBytes(blockedSpoolPath, [1, 2, 3]);

        var hostDllPath = ResolveYaguraHostDllPath();
        Assert.True(File.Exists(hostDllPath), $"Yagura.Host.dll が見つからない: {hostDllPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(hostDllPath);

        startInfo.Environment["YAGURA_DATAROOT"] = _dataRoot;
        startInfo.Environment["YAGURA_HTTP_PORT"] = "0";
        startInfo.Environment["YAGURA_UDP_PORT"] = "0";
        startInfo.Environment["YAGURA_TCP_PORT"] = "0";

        _hostProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdoutLines = new List<string>();
        var stdoutLock = new object();
        var udpPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var degradedWarningTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _hostProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stdoutLock)
            {
                stdoutLines.Add(e.Data);
            }

            var udpMatch = Regex.Match(e.Data, $@"{Regex.Escape(UdpListenerLogPrefix)}\s+(\d+)");
            if (udpMatch.Success && int.TryParse(udpMatch.Groups[1].Value, out var udpPort))
            {
                udpPortTcs.TrySetResult(udpPort);
            }

            if (e.Data.Contains(SpoolDegradedLogMarker, StringComparison.Ordinal))
            {
                degradedWarningTcs.TrySetResult(true);
            }
        };

        _hostProcess.Start();
        _hostProcess.BeginOutputReadLine();
        _hostProcess.BeginErrorReadLine();

        var udpPort = await WaitWithTimeoutAsync(udpPortTcs.Task, StartupTimeout, "UDP リスナ起動ログ");

        // 縮退運転の警告ログが出ることを確認する（イベントログへも同じ ILogger 経路で
        // 到達する。Program.cs の AddEventLog 参照。コンソール実行時は EventLog に加えて
        // コンソールにも出るため、標準出力からの検出で足りる）。
        await WaitWithTimeoutAsync(degradedWarningTcs.Task, StartupTimeout, "スプールなし縮退運転の警告ログ");

        // 縮退運転でも受信は止まっていないことを、実際に UDP でメッセージを送って確認する。
        var marker = $"e2e-degraded-{Guid.NewGuid():N}";
        using (var udpClient = new UdpClient())
        {
            var payload = Encoding.UTF8.GetBytes($"<134>degraded mode still receives: {marker}");
            await udpClient.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, udpPort));
        }

        // 受信が継続していることの確認は「プロセスが落ちていない」ことと「送信が例外なく
        // 完了する」ことで足りる（縮退中は DB 経路自体は正常なので、閲覧ページにも通常どおり
        // 現れるはずだが、本テストの主眼は「受信が止まらないこと」と「警告が出ること」の
        // 2 点であるため、閲覧ページの確認は ZeroConfigFirstRunE2ETests に譲る）。
        Assert.False(_hostProcess.HasExited, "縮退運転で起動したはずのプロセスが終了している。");

        _hostProcess.Kill(entireProcessTree: true);
        var exited = _hostProcess.WaitForExit((int)ShutdownTimeout.TotalMilliseconds);
        Assert.True(exited, "Yagura.Host の停止（Kill）がタイムアウトした。");
    }

    private static string ResolveYaguraHostDllPath() =>
        Path.Combine(AppContext.BaseDirectory, "Yagura.Host.dll");

    private static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, string description)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            throw new TimeoutException($"{description} の待機がタイムアウトした（{timeout}）。");
        }

        return await task;
    }
}
