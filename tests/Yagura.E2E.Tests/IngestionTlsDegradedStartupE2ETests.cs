using System.Diagnostics;

namespace Yagura.E2E.Tests;

/// <summary>
/// TLS 受信（RFC 5425。opt-in。security.md §6。Issue #137）を有効化したが証明書を解決できない
/// 場合の縮小継続（configuration.md §4.1 と同型。イベント ID 1016）の E2E 確認
/// （PR #225 レビュー指摘 Medium——Program.cs の証明書ストア解決失敗経路に自動テストが無かった）。
/// </summary>
/// <remarks>
/// <para>
/// <b>証明書をストアへ導入しないシナリオ</b>: 拇印としては正しい形式（16 進 40 桁）だが
/// LocalMachine\My ストアに存在しない値を指定する。静的な設定検証（<c>YaguraConfigurationLoader</c>）は
/// 通過するが、実際の証明書ストア参照（<c>CertificateProvider.Load</c>）が失敗するため、
/// <b>TLS 受信の bind エントリのみを開かずに縮小継続する</b>（起動時警告 1016）。平文 UDP/TCP 受信は
/// 一切影響を受けない（ADR-0004 決定 3）。管理リスナのリモート HTTPS の同型シナリオ（1013。
/// <c>AdminRemoteBindingRegressionTests</c>）と異なり、<b>ストアへ実証明書を導入する必要が無い</b>
/// ——「解決できない」ことそのものを検証するため。
/// </para>
/// <para>
/// ロケール非依存の ASCII トークン（<c>[1016]</c>・<c>ingestion-tls-certificate-unavailable</c>・
/// <c>UDP/TCP syslog listener started on port</c>）で照合する（<c>SpoolDegradedStartupE2ETests</c>
/// と同じ理由——en-US ランナーのコードページで日本語本文が文字化けする実障害を避ける）。
/// </para>
/// </remarks>
public sealed class IngestionTlsDegradedStartupE2ETests : IDisposable
{
    private const string UdpListenerLogPrefix = "UDP syslog listener started on port";
    private const string TcpListenerLogPrefix = "TCP syslog listener started on port";
    private const string TlsListenerLogPrefix = "TLS syslog listener started on port";
    private const string TlsCertUnavailableMarker = "ingestion-tls-certificate-unavailable";
    private const string TlsCertUnavailableEventId = "[1016]";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-tls-degraded-{Guid.NewGuid():N}");
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
    public async Task TlsEnabledWithUnresolvableThumbprint_HostStartsNormally_PlaintextUnaffected_TlsListenerNeverStarts_Logs1016()
    {
        Directory.CreateDirectory(_dataRoot);

        // 拇印としては正しい形式（16 進 40 桁）だが、ストアに存在しない証明書を指す。
        var nonexistentThumbprint = new string('F', 40);
        var configJson = $$"""
            {
              "Ingestion": {
                "Tls": {
                  "Enabled": "true",
                  "CertificateThumbprint": "{{nonexistentThumbprint}}"
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
        startInfo.Environment["YAGURA_INGESTION_TLS_PORT"] = "0";

        _hostProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdoutLines = new List<string>();
        var stdoutLock = new object();
        var udpStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcpStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var certUnavailableTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tlsStartedObserved = false;

        void OnData(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stdoutLock)
            {
                stdoutLines.Add(e.Data);
            }

            if (e.Data.Contains(UdpListenerLogPrefix, StringComparison.Ordinal))
            {
                udpStartedTcs.TrySetResult(true);
            }

            if (e.Data.Contains(TcpListenerLogPrefix, StringComparison.Ordinal))
            {
                tcpStartedTcs.TrySetResult(true);
            }

            if (e.Data.Contains(TlsListenerLogPrefix, StringComparison.Ordinal))
            {
                // TLS 受信リスナが起動してしまったら縮小継続していない——即座に検知する。
                tlsStartedObserved = true;
            }

            if (e.Data.Contains(TlsCertUnavailableMarker, StringComparison.Ordinal) ||
                e.Data.Contains(TlsCertUnavailableEventId, StringComparison.Ordinal))
            {
                certUnavailableTcs.TrySetResult(true);
            }
        }

        _hostProcess.OutputDataReceived += OnData;
        _hostProcess.ErrorDataReceived += OnData;

        _hostProcess.Start();
        _hostProcess.BeginOutputReadLine();
        _hostProcess.BeginErrorReadLine();

        // 平文 UDP/TCP 受信は縮小継続の影響を受けず、通常どおり起動する（ADR-0004 決定 3）。
        await WaitWithTimeoutAsync(udpStartedTcs.Task, StartupTimeout, "UDP リスナ起動ログ");
        await WaitWithTimeoutAsync(tcpStartedTcs.Task, StartupTimeout, "TCP リスナ起動ログ");

        // 証明書解決失敗による縮小継続の起動時警告（イベント ID 1016）が出る。
        await WaitWithTimeoutAsync(certUnavailableTcs.Task, StartupTimeout, "TLS 証明書解決失敗（1016）の警告ログ");

        // TLS 受信リスナは起動しない（縮小継続——起動ログが「その後も」現れないことを一定時間確認する）。
        // UDP/TCP 起動 + 1016 警告が出た後に TLS リスナが起動する経路は無い（Program.cs は証明書を
        // 解決できた場合のみ tlsListenerOptions を構成する）ため、短い追加待機で足りる。
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(tlsStartedObserved, "証明書を解決できないのに TLS 受信リスナが起動してしまった（縮小継続していない）。");

        // 起動は中止しない（fail-closed の対象外——環境依存の縮小継続）。
        Assert.False(_hostProcess.HasExited, "縮小継続で起動を継続するはずのプロセスが終了している。");

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
}
