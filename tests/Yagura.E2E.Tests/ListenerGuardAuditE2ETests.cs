using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace Yagura.E2E.Tests;

/// <summary>
/// 閲覧リスナへの管理系要求の拒否 + 監査記録の最小基盤（M6-2。Issue #52。security.md §1
/// L-3b・§4）の E2E smoke テスト。
/// </summary>
/// <remarks>
/// <see cref="ListenerSeparationE2ETests"/>（M6-1）が「拒否そのもの」を検証したのに対し、
/// 本テストは「拒否 + 監査記録」——閲覧ポート経由の <c>/admin</c> 要求が 404 になったうえで、
/// データルート配下の監査記録ファイル（<c>audit/audit.jsonl</c>）に 1 行残ることを、実際の
/// Yagura.Host 実行ファイルを子プロセスとして起動して確認する。
/// </remarks>
public sealed class ListenerGuardAuditE2ETests : IDisposable
{
    private const string UdpListenerLogPrefix = "UDP syslog listener started on port";

    private static readonly Regex ViewerListeningPortPattern =
        new(@"Now listening on:\s*http://\[::\]:(\d+)\s*$", RegexOptions.Compiled);

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-audit-{Guid.NewGuid():N}");
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
                // ベストエフォート（他 E2E テストと同じ判断）。
            }
        }
    }

    [Fact]
    public async Task ViewerPort_AdminRequest_ReturnsNotFound_AndLeavesOneAuditLine()
    {
        Directory.CreateDirectory(_dataRoot);

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

        var udpPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        _hostProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            if (e.Data.Contains(UdpListenerLogPrefix, StringComparison.Ordinal))
            {
                udpPortTcs.TrySetResult(0);
            }

            var viewerMatch = ViewerListeningPortPattern.Match(e.Data);
            if (viewerMatch.Success && int.TryParse(viewerMatch.Groups[1].Value, out var viewerPort))
            {
                viewerPortTcs.TrySetResult(viewerPort);
            }
        };

        _hostProcess.Start();
        _hostProcess.BeginOutputReadLine();
        _hostProcess.BeginErrorReadLine();

        await WaitWithTimeoutAsync(udpPortTcs.Task, StartupTimeout, "UDP リスナ起動ログ");
        var viewerPort = await WaitWithTimeoutAsync(viewerPortTcs.Task, StartupTimeout, "閲覧リスナ HTTP listening ログ");

        // security.md §1 L-3b: 閲覧ポート経由での管理系要求(/admin)は 404 になる。
        using (var viewerClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{viewerPort}") })
        {
            var response = await PollForResponseAsync(viewerClient, "/admin", StartupTimeout);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // security.md §4.1・§4.2: 拒否の監査記録がデータルート配下の audit/audit.jsonl に
        // 1 行残ることを確認する。プロセス側の非同期書き込み完了を待つため、ファイルの
        // 出現・内容確認は短時間のポーリングで行う（他 E2E テストの起動待ちと同じ判断）。
        var auditFilePath = Path.Combine(_dataRoot, "audit", "audit.jsonl");
        var lines = await PollForAuditLinesAsync(auditFilePath, expectedCount: 1, StartupTimeout);

        var line = Assert.Single(lines);
        // ロケール非依存の ASCII トークンで照合する(他 E2E テストと同じ理由——リダイレクトされた
        // 子プロセス stdout ではなくファイル読み取りだが、JSON 内容自体は英数字のみで構成される
        // ため文字化けの懸念はない。念のため事象種別の識別子で確認する)。
        Assert.Contains("ViewerListenerAdminRequestRejected", line, StringComparison.Ordinal);
        Assert.Contains("\"AttemptedPath\":\"/admin\"", line, StringComparison.Ordinal);
        Assert.Contains($"\"ReachedListenerPort\":{viewerPort}", line, StringComparison.Ordinal);

        _hostProcess.Kill(entireProcessTree: true);
        var exited = _hostProcess.WaitForExit((int)ShutdownTimeout.TotalMilliseconds);
        Assert.True(exited, "Yagura.Host の停止（Kill）がタイムアウトした。");
    }

    private static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, string description)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            throw new TimeoutException($"{description} の待機がタイムアウトした（{timeout}）。");
        }

        return await task;
    }

    /// <summary>
    /// サーバが listen を完了する直前のタイミングでの一過性失敗を許容するポーリング
    /// （ZeroConfigFirstRunE2ETests.PollUntilAsync と同じ判断）。
    /// </summary>
    private static async Task<HttpResponseMessage> PollForResponseAsync(HttpClient client, string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                return await client.GetAsync(path);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
        }

        return await client.GetAsync(path);
    }

    /// <summary>
    /// 監査記録ファイルへの書き込みは要求処理完了後の非同期処理であるため、ファイルの出現・
    /// 行数到達をポーリングで待つ。
    /// </summary>
    private static async Task<string[]> PollForAuditLinesAsync(string filePath, int expectedCount, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(filePath);
                    if (lines.Length >= expectedCount)
                    {
                        return lines;
                    }
                }
                catch (IOException)
                {
                    // 書き込み中の排他と衝突した場合は次のポーリングへ。
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException(
            $"監査記録ファイル {filePath} に {expectedCount} 行到達するまでの待機がタイムアウトした（{timeout}）。");
    }
}
