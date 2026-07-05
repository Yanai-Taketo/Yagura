using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace Yagura.E2E.Tests;

/// <summary>
/// リスナ分離（M6-1。Issue #51）の E2E smoke テスト。
/// </summary>
/// <remarks>
/// security.md §1 L-3b の前提となる「管理系ルートが閲覧リスナ経由で絶対に実行されない」
/// 構造を、実際の Yagura.Host 実行ファイルを子プロセスとして起動して確認する。
/// 単体テスト（<c>ListenerPortGuardMiddlewareTests</c>・<c>ListenerBindPlanTests</c>）は
/// 判定ロジック・bind 先計算のみを検証するため、本テストは実際に 2 つの TCP ポートで
/// 待ち受けた実サーバに対して HTTP リクエストを送ることで、配線全体（Kestrel の bind・
/// ミドルウェア登録順序・ルーティング）が意図どおりに機能することを確認する。
/// </remarks>
public sealed class ListenerSeparationE2ETests : IDisposable
{
    private const string UdpListenerLogPrefix = "UDP syslog listener started on port";

    private static readonly Regex ViewerListeningPortPattern =
        new(@"Now listening on:\s*http://\[::\]:(\d+)\s*$", RegexOptions.Compiled);

    // 管理リスナは常に loopback の具体アドレスでログに出る（127.0.0.1 と [::1] の 2 行。
    // ListenerBindPlan の ResolvePortForDualStackLoopback により両方とも同じポートになる
    // ——どちらか一方のログ行から拾えば足りる）。
    private static readonly Regex AdminListeningPortPattern =
        new(@"Now listening on:\s*http://127\.0\.0\.1:(\d+)\s*$", RegexOptions.Compiled);

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-listener-{Guid.NewGuid():N}");
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
    public async Task AdminEndpoint_ViaAdminPort_IsReachable_ViaViewerPort_Returns404()
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
        var adminPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        _hostProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            // UDP リスナ起動を待たないと(他 E2E テストと同様)起動途中でのアクセスになり得るため、
            // 目印として利用する(本テスト自体は UDP メッセージを送らない)。
            if (e.Data.Contains(UdpListenerLogPrefix, StringComparison.Ordinal))
            {
                udpPortTcs.TrySetResult(0);
            }

            var viewerMatch = ViewerListeningPortPattern.Match(e.Data);
            if (viewerMatch.Success && int.TryParse(viewerMatch.Groups[1].Value, out var viewerPort))
            {
                viewerPortTcs.TrySetResult(viewerPort);
            }

            var adminMatch = AdminListeningPortPattern.Match(e.Data);
            if (adminMatch.Success && int.TryParse(adminMatch.Groups[1].Value, out var adminPort))
            {
                adminPortTcs.TrySetResult(adminPort);
            }
        };

        _hostProcess.Start();
        _hostProcess.BeginOutputReadLine();
        _hostProcess.BeginErrorReadLine();

        await WaitWithTimeoutAsync(udpPortTcs.Task, StartupTimeout, "UDP リスナ起動ログ");
        var viewerPort = await WaitWithTimeoutAsync(viewerPortTcs.Task, StartupTimeout, "閲覧リスナ HTTP listening ログ");
        var adminPort = await WaitWithTimeoutAsync(adminPortTcs.Task, StartupTimeout, "管理リスナ HTTP listening ログ");

        Assert.NotEqual(viewerPort, adminPort);

        // 管理系エンドポイント(/admin)は管理ポート経由で到達できる。
        using (var adminClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{adminPort}") })
        {
            var adminResponse = await PollForResponseAsync(adminClient, "/admin", StartupTimeout);
            Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        }

        // security.md §1 L-3b の前提: 閲覧ポート経由では管理系エンドポイント(/admin)へ
        // 到達できず 404 になる(「拒否 + 監査記録」自体は後続 Issue #52 のスコープ)。
        using (var viewerClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{viewerPort}") })
        {
            var viewerToAdminResponse = await viewerClient.GetAsync("/admin");
            Assert.Equal(HttpStatusCode.NotFound, viewerToAdminResponse.StatusCode);

            // 設計判断の確認: 閲覧系ルート("/")は閲覧ポート経由で通常どおり到達できる。
            var viewerRootResponse = await viewerClient.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, viewerRootResponse.StatusCode);
        }

        // 設計判断の確認: 閲覧系ルート("/")は管理ポート経由でも到達できる(ui.md §4 の
        // 不変条件は「閲覧リスナに書き込み系を置かない」であり、管理リスナからの閲覧系
        // 到達を妨げる理由はない——管理者がローカルで全部見られることはむしろ自然)。
        using (var adminClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{adminPort}") })
        {
            var adminToViewerResponse = await adminClient.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, adminToViewerResponse.StatusCode);
        }

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
}
