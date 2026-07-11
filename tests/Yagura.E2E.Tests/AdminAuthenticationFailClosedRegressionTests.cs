using System.Diagnostics;
using System.Text;

namespace Yagura.E2E.Tests;

/// <summary>
/// loopback 認証 opt-in（ADR-0010 決定 1）の fail-closed 不変条件の実プロセス回帰テスト。
/// **Phase 1 受け入れ条件 (v)**（ADR-0010 決定 8）。
/// </summary>
/// <remarks>
/// <para>
/// <c>Admin:Authentication:RequireForLoopback = true</c> かつ Windows 統合認証・アプリ独自認証の
/// いずれも有効化されていない設定は、<see cref="Yagura.Host.Configuration.YaguraConfigurationLoader.Load"/>
/// が起動失敗として拒否する（configuration.md §1「起動失敗」分類。既存 L-4 系（設定がどう
/// 壊れていても管理リスナが loopback 以外へ束縛されない）と対称の、認証なし + loopback 認証
/// opt-in 有効という設定の組み合わせに対する fail-closed 不変条件）。
/// </para>
/// <para>
/// <b>アサーション観点</b>: (1) プロセスが listen を開始せず、非 0 の終了コードで終了する
/// （<see cref="LoopbackBindingRegressionTests"/> の「listen を開始する」系シナリオとは検証形が
/// 異なる——起動そのものを止める）。(2) 標準出力/標準エラーに「なぜ起動しないか・何を直せば
/// よいか」の具体的誘導文言が含まれる（ADR-0010 決定 1・委任事項 5。佐藤ペルソナの指摘）。
/// (3) イベント ID 1009（<c>ConfigurationEventIds.AdminAuthenticationFailClosedStartupRejected</c>）
/// が Critical ログの EventId として記録される。
/// </para>
/// </remarks>
public sealed class AdminAuthenticationFailClosedRegressionTests : IDisposable
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    private readonly List<string> _dataRoots = new();
    private readonly List<Process> _processes = new();

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }

        foreach (var dataRoot in _dataRoots)
        {
            try
            {
                if (Directory.Exists(dataRoot))
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ベストエフォート(kill 直後は SQLite の -shm/-wal 補助ファイルへの OS
                // ハンドル解放が非同期に遅れることがある——後続テストの動作には影響しない)。
            }
        }
    }

    [Fact]
    public async Task RequireForLoopback_WithNoAuthMethodConfigured_ProcessExitsNonZero_WithGuidanceMessage()
    {
        var (exitCode, output) = await RunHostProcessToExitAsync("""
            {
              "Admin": { "Authentication": { "RequireForLoopback": "true" } }
            }
            """);

        Assert.NotEqual(0, exitCode);

        // 「なぜ起動しないか・何を直せばよいか」の具体的誘導文言(ADR-0010 決定1・委任事項5)。
        Assert.Contains("RequireForLoopback", output, StringComparison.Ordinal);
        Assert.Contains("Admin:Authentication:Windows:Enabled", output, StringComparison.Ordinal);
        Assert.Contains("Admin:Authentication:App:Enabled", output, StringComparison.Ordinal);

        // イベント ID 1011 が Critical ログの EventId として記録されること
        // (既定コンソールロガーの出力形式 "warn: Category[EventId]" のうち EventId 部分。
        // 1009 = Issue #152、1010 = PR #211 が使用のため本イベントは 1011——
        // ConfigurationEventIds.AdminAuthenticationFailClosedStartupRejected 参照)。
        Assert.Contains("[1011]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireForLoopback_WithNoAuthMethodConfigured_NeverStartsListening()
    {
        var (_, output) = await RunHostProcessToExitAsync("""
            {
              "Admin": { "Authentication": { "RequireForLoopback": "true" } }
            }
            """);

        // 起動失敗のため、リスナの listen 開始ログが一切現れないこと
        // (LoopbackBindingRegressionTests の「listen を開始する」系シナリオとの対比)。
        Assert.DoesNotContain("Now listening on:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireForLoopback_WithWindowsAuthEnabled_StartsNormally()
    {
        // fail-closed の対称確認: 認証方式が構成されていれば、loopback 認証 opt-in を
        // 有効にしても起動を拒否しない(ADR-0010 決定1の要求はあくまで「認証方式ゼロ構成」の
        // 組み合わせに対する拒否であり、opt-in 自体を禁止するものではない)。
        var (process, adminPort) = await StartHostProcessAsync("""
            {
              "Admin": { "Authentication": { "Windows": { "Enabled": "true" }, "RequireForLoopback": "true" } }
            }
            """);

        try
        {
            Assert.True(adminPort > 0);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task DefaultNoAuth_LoopbackRazorAdminPages_RenderWithoutRedirectingToLogin()
    {
        // 回帰(issue #242): 既定(ゼロ設定・無認証)で loopback 経由の Razor 管理ページ(AdminScreenLayout を
        // 通る /admin・/admin/setup・/admin/auth-setup)が、ログイン画面へ 302 されず描画されること。
        //
        // AdminScreenLayout の認証充足判定は、SSR/prerender(circuit 未確立)では
        // CircuitContext.IsLoopbackListener が null のため、接続の実ローカルポート(HttpContext)で
        // loopback を判定しなければならない。これを誤ると既定無認証でも Razor 管理ページが
        // /admin/login へ 302 され、初期セットアップ・認証有効化にすら到達できなくなる(P0)。
        // 既存の loopback 検証は素の HTTP エンドポイント(/admin/forwarder-kit/download)のみを見ており、
        // AdminScreenLayout を通る Razor ページを見ていなかったため本回帰を取りこぼしていた。
        var (process, adminPort) = await StartHostProcessAsync("{}");

        try
        {
            using var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false });
            foreach (var path in new[] { "/admin", "/admin/setup", "/admin/auth-setup" })
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await client.GetAsync($"http://127.0.0.1:{adminPort}{path}", cts.Token);

                Assert.True(
                    response.StatusCode == System.Net.HttpStatusCode.OK,
                    $"既定無認証の loopback で {path} が描画されず {(int)response.StatusCode} を返した" +
                    $"(Location: {response.Headers.Location?.ToString() ?? "なし"})。" +
                    "AdminScreenLayout の SSR loopback 判定の回帰(issue #242)。");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private async Task<(int ExitCode, string Output)> RunHostProcessToExitAsync(string configJson)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-authfailclosed-{Guid.NewGuid():N}");
        _dataRoots.Add(dataRoot);
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "yagura.json"), configJson);

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
        startInfo.Environment["YAGURA_DATAROOT"] = dataRoot;
        startInfo.Environment["YAGURA_HTTP_PORT"] = "0";
        startInfo.Environment["YAGURA_UDP_PORT"] = "0";
        startInfo.Environment["YAGURA_TCP_PORT"] = "0";
        startInfo.Environment["YAGURA_ADMIN_PORT"] = "0";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _processes.Add(process);

        var output = new StringBuilder();
        var gate = new object();

        void OnData(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (gate)
            {
                output.AppendLine(e.Data);
            }
        }

        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(ExitTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException(
                $"fail-closed 拒否を期待したが、プロセスが {ExitTimeout} 以内に終了しなかった " +
                $"(listen を開始したまま起動し続けている可能性がある)。出力:\n{output}");
        }

        lock (gate)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    private async Task<(Process Process, int AdminPort)> StartHostProcessAsync(string configJson)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-authfailclosed-{Guid.NewGuid():N}");
        _dataRoots.Add(dataRoot);
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "yagura.json"), configJson);

        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Yagura.Host.dll");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(hostDllPath);
        startInfo.Environment["YAGURA_DATAROOT"] = dataRoot;
        startInfo.Environment["YAGURA_HTTP_PORT"] = "0";
        startInfo.Environment["YAGURA_UDP_PORT"] = "0";
        startInfo.Environment["YAGURA_TCP_PORT"] = "0";
        startInfo.Environment["YAGURA_ADMIN_PORT"] = "0";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _processes.Add(process);

        var adminPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adminPattern = new System.Text.RegularExpressions.Regex(@"Now listening on:\s*http://127\.0\.0\.1:(\d+)\s*$");

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            var match = adminPattern.Match(e.Data);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var port))
            {
                adminPortTcs.TrySetResult(port);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(ExitTimeout);
        cts.Token.Register(() => adminPortTcs.TrySetCanceled());

        var adminPort = await adminPortTcs.Task;
        return (process, adminPort);
    }
}
