using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Yagura.TestSupport;

namespace Yagura.E2E.Tests;

/// <summary>
/// 保存先（SQL Server）が到達不能でも、プロセスが起動しきり受信を開始することの E2E 確認
/// （ADR-0023 決定 1 受け入れ基準①。Issue #466）。
/// </summary>
/// <remarks>
/// <para>
/// <b>回帰対象</b>: 修正前は起動シーケンスの管理者アカウントストア初期化が try/catch なしで
/// 待たれており、保存先が到達不能だと <c>SqlException</c> が未処理例外となりプロセスが即死した
/// （<c>APPCRASH e0434352</c>。実機 lab で検出）。受信パイプラインは同じ障害でスプールへ正しく
/// 縮退するのに、初期化だけが縮退せず「DB 障害中は再起動できない」状態を作っていた。
/// </para>
/// <para>
/// <b>到達不能の作り方</b>: SQL Server を用意せず、**誰も listen していない loopback ポート**を
/// 接続先に指定する。実 DB も AD も不要なため fork からの PR でも自己確認できる（ADR-0023
/// 受け入れ基準のリサの質問 3 と同じ趣旨）。接続文字列に <c>Connect Timeout</c> を明示するのは、
/// 既定値（15 秒）に依存すると本テストの所要時間が provider の既定に左右されるため。
/// </para>
/// </remarks>
public sealed class StorageUnreachableStartupE2ETests : IDisposable
{
    private const string UdpListenerLogPrefix = "UDP syslog listener started on port";

    /// <summary>
    /// ロケール非依存の ASCII トークンで照合する（<c>SpoolDegradedStartupE2ETests</c> と同じ判断
    /// ——日本語本文での照合は、子プロセス stdout のコードページが日本語を表現できない環境
    /// （en-US の CI ランナー）で文字化けし一致しない実障害があった）。
    /// </summary>
    private const string StorageDegradedLogMarker = "[admin-account-store-unavailable]";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly TestTempDirectory _tempDataRoot = new("e2e-storage-unreachable");

    private Process? _hostProcess;

    private string _dataRoot => _tempDataRoot.Path;

    public void Dispose()
    {
        if (_hostProcess is { HasExited: false })
        {
            _hostProcess.Kill(entireProcessTree: true);
        }

        _hostProcess?.Dispose();

        // ベストエフォート（他 E2E テストと同じ判断）。
        _tempDataRoot.Dispose();
    }

    [Fact]
    public async Task SqlServerUnreachable_HostStartsAndReceives_AndLogsDegradedWarning()
    {
        Directory.CreateDirectory(_dataRoot);

        var deadPort = ReserveUnusedTcpPort();
        var configJson = $$"""
            {
              "Storage": {
                "Provider": "sqlserver",
                "SqlServer": {
                  "ConnectionString": "Server=127.0.0.1,{{deadPort}};Database=YaguraE2E;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3"
                }
              }
            }
            """;
        File.WriteAllText(Path.Combine(_dataRoot, "yagura.json"), configJson);

        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Yagura.Host.dll");
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
        startInfo.Environment["YAGURA_ADMIN_PORT"] = "0";

        _hostProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdoutLines = new List<string>();
        var stdoutLock = new object();
        var udpPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var degradedWarningTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (stdoutLock)
            {
                stdoutLines.Add(line);
            }

            var udpMatch = Regex.Match(line, $@"{Regex.Escape(UdpListenerLogPrefix)}\s+(\d+)");
            if (udpMatch.Success && int.TryParse(udpMatch.Groups[1].Value, out var udpPort))
            {
                udpPortTcs.TrySetResult(udpPort);
            }

            if (line.Contains(StorageDegradedLogMarker, StringComparison.Ordinal))
            {
                degradedWarningTcs.TrySetResult(true);
            }
        }

        _hostProcess.OutputDataReceived += (_, e) => OnLine(e.Data);
        _hostProcess.ErrorDataReceived += (_, e) => OnLine(e.Data);

        _hostProcess.Start();
        _hostProcess.BeginOutputReadLine();
        _hostProcess.BeginErrorReadLine();

        var udpPort = await WaitWithTimeoutAsync(
            udpPortTcs.Task, StartupTimeout, "UDP リスナ起動ログ", stdoutLines, stdoutLock);

        // 縮退が**見えている**こと（ADR-0023 決定 1 の「可視化された縮退」。起動を継続させる
        // 以上、縮退していることが必ず観測できるのが成立条件）。
        await WaitWithTimeoutAsync(
            degradedWarningTcs.Task, StartupTimeout, "保存先縮退の能動通知（1039）", stdoutLines, stdoutLock);

        // 受信が実際に動いていること（プロセスが生きているだけでは基準①を満たさない）。
        var marker = $"e2e-storage-unreachable-{Guid.NewGuid():N}";
        using (var udpClient = new UdpClient())
        {
            var payload = Encoding.UTF8.GetBytes($"<134>storage unreachable but still receiving: {marker}");
            await udpClient.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, udpPort));
        }

        Assert.False(
            _hostProcess.HasExited,
            "保存先が到達不能でも起動を継続するはずのプロセスが終了している（Issue #466 の回帰）。");

        _hostProcess.Kill(entireProcessTree: true);
        var exited = _hostProcess.WaitForExit((int)ShutdownTimeout.TotalMilliseconds);
        Assert.True(exited, "Yagura.Host の停止（Kill）がタイムアウトした。");
    }

    /// <summary>
    /// 「誰も listen していない」TCP ポート番号を得る。OS に採番させた直後に閉じることで、
    /// 少なくとも直後は接続が拒否される番号を選ぶ（固定番号のハードコードは、CI ランナーで
    /// 別プロセスが偶然使っていると接続が成立してテストの前提が崩れるため避ける）。
    /// </summary>
    private static int ReserveUnusedTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<T> WaitWithTimeoutAsync<T>(
        Task<T> task, TimeSpan timeout, string description, List<string> stdoutLines, object stdoutLock)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            string captured;
            lock (stdoutLock)
            {
                captured = string.Join(Environment.NewLine, stdoutLines);
            }

            throw new TimeoutException(
                $"{description} の待機がタイムアウトした（{timeout}）。取得済みの出力:{Environment.NewLine}{captured}");
        }

        return await task;
    }
}
