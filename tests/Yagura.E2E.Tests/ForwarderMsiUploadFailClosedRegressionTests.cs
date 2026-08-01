using System.Diagnostics;
using System.Text;
using Yagura.TestSupport;

namespace Yagura.E2E.Tests;

/// <summary>
/// フォワーダ MSI アップロード opt-in（ADR-0020 決定 1）の fail-closed 不変条件の実プロセス
/// 回帰テスト（決定 5 ①——1011/1012 と同型の子プロセス E2E。単体テスト側は
/// <c>Yagura.Host.Tests.Configuration.ForwarderMsiUploadConfigurationTests</c>）。
/// </summary>
/// <remarks>
/// <c>Admin:ForwarderKit:MsiUpload:Enabled = true</c> かつ前提条件（管理 UI 認証のいずれかが
/// 有効——ADR-0021 決定 2 で <c>RequireForLoopback</c> の必須化は撤廃）が欠けた設定は、
/// 起動失敗（EventId 1032）として拒否される。エラーメッセージには復旧に必要な具体の設定キーと
/// 値（<c>false に戻す</c>）が含まれる（委任 1——手編集復旧の場面では UI の誘導が使えないため）。
/// 逆に、認証構成済み + RequireForLoopback 無しの構成は ADR-0021 以降は正当な有効化構成であり、
/// 起動が成立することも実プロセスで固定する（旧 (ii) 検証の削除の回帰防止）。
/// </remarks>
public sealed class ForwarderMsiUploadFailClosedRegressionTests : IDisposable
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    private readonly List<TestTempDirectory> _dataRoots = new();
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

        // ベストエフォート（AdminAuthenticationFailClosedRegressionTests と同じ判断）。
        foreach (var dataRoot in _dataRoots)
        {
            dataRoot.Dispose();
        }
    }

    [Fact]
    public async Task MsiUploadEnabled_WithAuthButWithoutRequireForLoopback_StartsListening()
    {
        // ADR-0021 決定 2: 認証方式が構成済みなら RequireForLoopback = false（既定）でも
        // 有効化できる——旧仕様（ADR-0020 決定 1 (ii)）では 1032 起動拒否だった構成が
        // 起動に至ることを実プロセスで固定する（無認証 loopback からの到達遮断は
        // アップロード操作単位の専用認可ポリシーが担うため、構成レベルでは拒否しない）。
        var output = await RunHostProcessUntilListeningAsync("""
            {
              "Admin": {
                "Authentication": { "App": { "Enabled": "true" } },
                "ForwarderKit": { "MsiUpload": { "Enabled": "true" } }
              }
            }
            """);

        Assert.Contains("Now listening on:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[1032]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MsiUploadEnabled_WithoutAnyAuthMethod_ProcessExitsNonZero_WithGuidanceMessage()
    {
        var (exitCode, output) = await RunHostProcessToExitAsync("""
            {
              "Admin": { "ForwarderKit": { "MsiUpload": { "Enabled": "true" } } }
            }
            """);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("Now listening on:", output, StringComparison.Ordinal);

        // 「なぜ起動しないか・何を直せばよいか」の誘導（ADR-0020 委任 1——復旧に必要な
        // 具体の設定キーを明記する）。前提条件は認証方式のみ（ADR-0021 決定 2）——
        // RequireForLoopback はメッセージに現れない。
        Assert.Contains("Admin:Authentication:Windows:Enabled", output, StringComparison.Ordinal);
        Assert.Contains("Admin:ForwarderKit:MsiUpload:Enabled", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Admin:Authentication:RequireForLoopback", output, StringComparison.Ordinal);

        // イベント ID 1032（ConfigurationEventIds.ForwarderMsiUploadFailClosedStartupRejected）。
        Assert.Contains("[1032]", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 起動が成立する（listen 開始まで到達する）ことを検証するための起動ヘルパー。listen 開始を
    /// 観測したらプロセスを停止する。fail-closed で即終了した場合は出力全文を含めて失敗させる。
    /// </summary>
    private async Task<string> RunHostProcessUntilListeningAsync(string configJson)
    {
        var (process, output, gate) = StartHostProcess(configJson);

        var deadline = DateTime.UtcNow + ExitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
            {
                if (output.ToString().Contains("Now listening on:", StringComparison.Ordinal))
                {
                    return output.ToString();
                }
            }

            if (process.HasExited)
            {
                lock (gate)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"起動の成立を期待したが、プロセスが exit code {process.ExitCode} で終了した。出力:\n{output}");
                }
            }

            await Task.Delay(200);
        }

        lock (gate)
        {
            throw new Xunit.Sdk.XunitException(
                $"{ExitTimeout} 以内に listen 開始を観測できなかった。出力:\n{output}");
        }
    }

    private async Task<(int ExitCode, string Output)> RunHostProcessToExitAsync(string configJson)
    {
        var (process, output, gate) = StartHostProcess(configJson);

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

    private (Process Process, StringBuilder Output, object Gate) StartHostProcess(string configJson)
    {
        var tempDataRoot = new TestTempDirectory("e2e-msiupload-failclosed");
        _dataRoots.Add(tempDataRoot);
        var dataRoot = tempDataRoot.Path;
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

        return (process, output, gate);
    }
}
