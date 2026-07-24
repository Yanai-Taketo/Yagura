using System.Diagnostics;
using System.Text;

namespace Yagura.E2E.Tests;

/// <summary>
/// フォワーダ MSI アップロード opt-in（ADR-0020 決定 1）の fail-closed 不変条件の実プロセス
/// 回帰テスト（決定 5 ①——1011/1012 と同型の子プロセス E2E。単体テスト側は
/// <c>Yagura.Host.Tests.Configuration.ForwarderMsiUploadConfigurationTests</c>）。
/// </summary>
/// <remarks>
/// <c>Admin:ForwarderKit:MsiUpload:Enabled = true</c> かつ前提条件（管理 UI 認証のいずれか +
/// <c>Admin:Authentication:RequireForLoopback</c>）が欠けた設定は、起動失敗（EventId 1032）として
/// 拒否される。エラーメッセージには復旧に必要な具体の設定キーと値（<c>false に戻す</c>）が
/// 含まれる（委任 1——手編集復旧の場面では UI の誘導が使えないため）。
/// </remarks>
public sealed class ForwarderMsiUploadFailClosedRegressionTests : IDisposable
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
                // ベストエフォート（AdminAuthenticationFailClosedRegressionTests と同じ判断）。
            }
        }
    }

    [Fact]
    public async Task MsiUploadEnabled_WithoutRequireForLoopback_ProcessExitsNonZero_WithGuidanceMessage()
    {
        var (exitCode, output) = await RunHostProcessToExitAsync("""
            {
              "Admin": {
                "Authentication": { "App": { "Enabled": "true" } },
                "ForwarderKit": { "MsiUpload": { "Enabled": "true" } }
              }
            }
            """);

        Assert.NotEqual(0, exitCode);

        // 「なぜ起動しないか・何を直せばよいか」の誘導（ADR-0020 決定 1・委任 1——復旧に必要な
        // 具体の設定キーを明記する。再レビュー鈴木指摘）。
        Assert.Contains("Admin:Authentication:RequireForLoopback", output, StringComparison.Ordinal);
        Assert.Contains("Admin:ForwarderKit:MsiUpload:Enabled", output, StringComparison.Ordinal);

        // イベント ID 1032（ConfigurationEventIds.ForwarderMsiUploadFailClosedStartupRejected）。
        Assert.Contains("[1032]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MsiUploadEnabled_WithoutAnyPrecondition_NeverStartsListening()
    {
        var (_, output) = await RunHostProcessToExitAsync("""
            {
              "Admin": { "ForwarderKit": { "MsiUpload": { "Enabled": "true" } } }
            }
            """);

        Assert.DoesNotContain("Now listening on:", output, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Output)> RunHostProcessToExitAsync(string configJson)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-msiupload-failclosed-{Guid.NewGuid():N}");
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
}
