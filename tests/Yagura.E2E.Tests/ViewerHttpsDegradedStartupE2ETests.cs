using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Yagura.TestSupport;

namespace Yagura.E2E.Tests;

/// <summary>
/// 閲覧 UI の HTTPS（ADR-0022。opt-in）を有効化したが証明書を解決できない場合の縮小継続
/// （イベント ID 1035）の E2E 確認。ADR-0022 決定 2「失敗時に受信を巻き添えにしない・平文へは
/// 決して落ちない・閲覧面は可視化つきで落ちてよい」を実プロセスで固定する
/// （受け入れ基準「証明書事故で受信・解析・保存・スプール・管理リスナが一切影響を受けない」の
/// 起動時経路。<c>IngestionTlsDegradedStartupE2ETests</c>（1016）と同型の構成）。
/// </summary>
/// <remarks>
/// <para>
/// <b>証明書をストアへ導入しないシナリオ</b>: 拇印としては正しい形式（16 進 40 桁）だが
/// LocalMachine\My ストアに存在しない値を指定する。静的な設定検証（<c>YaguraConfigurationLoader</c>——
/// <c>ViewerHttpsMode.Enabled</c>）は通過するが、実際の証明書ストア参照
/// （<c>CertificateProvider.Load</c>）が失敗するため、<b>閲覧リスナの bind エントリのみを開かずに
/// 縮小継続する</b>（起動時警告 1035）。<b>平文 HTTP でも開かない</b>——閲覧ポートへの TCP 接続が
/// 拒否されることまで確認する（「HTTPS が構成できないから平文で開く」への退行を検知する。
/// これが 1013/1016 の同型テストに無い本テスト固有の検証点）。
/// </para>
/// <para>
/// ロケール非依存の ASCII トークン（<c>[1035]</c>・<c>viewer-https-certificate-unavailable</c>・
/// <c>UDP/TCP syslog listener started on port</c>）で照合する（<c>IngestionTlsDegradedStartupE2ETests</c>
/// と同じ理由——en-US ランナーのコードページで日本語本文が文字化けする実障害を避ける）。
/// </para>
/// </remarks>
public sealed class ViewerHttpsDegradedStartupE2ETests : IDisposable
{
    private const string UdpListenerLogPrefix = "UDP syslog listener started on port";
    private const string TcpListenerLogPrefix = "TCP syslog listener started on port";
    private const string ViewerHttpsCertUnavailableMarker = "viewer-https-certificate-unavailable";
    private const string ViewerHttpsCertUnavailableEventId = "[1035]";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly TestTempDirectory _tempDataRoot = new("e2e-viewer-https-degraded");
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
    public async Task ViewerHttpsEnabledWithUnresolvableThumbprint_HostStartsNormally_IngestionUnaffected_ViewerPortNotOpen_Logs1035()
    {
        Directory.CreateDirectory(_dataRoot);

        // 拇印としては正しい形式（16 進 40 桁）だが、ストアに存在しない証明書を指す。
        var nonexistentThumbprint = new string('F', 40);
        var configJson = $$"""
            {
              "Viewer": {
                "Https": {
                  "Enabled": "true",
                  "CertificateThumbprint": "{{nonexistentThumbprint}}"
                }
              }
            }
            """;
        File.WriteAllText(Path.Combine(_dataRoot, "yagura.json"), configJson);

        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Yagura.Host.dll");
        Assert.True(File.Exists(hostDllPath), $"Yagura.Host.dll が見つからない: {hostDllPath}");

        // 閲覧ポートは 0（OS 採番）ではなく、テストが予約してから離した具体ポートを指定する——
        // 「そのポートに何も listen していない（平文にも落ちていない）」ことを接続試行で
        // 検証するため、ポート番号をテスト側が知っている必要がある。
        var viewerPort = ReserveEphemeralPort();

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
        startInfo.Environment["YAGURA_HTTP_PORT"] = viewerPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["YAGURA_UDP_PORT"] = "0";
        startInfo.Environment["YAGURA_TCP_PORT"] = "0";
        startInfo.Environment["YAGURA_ADMIN_PORT"] = "0";

        _hostProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var udpStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcpStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var certUnavailableTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnData(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            if (e.Data.Contains(UdpListenerLogPrefix, StringComparison.Ordinal))
            {
                udpStartedTcs.TrySetResult(true);
            }

            if (e.Data.Contains(TcpListenerLogPrefix, StringComparison.Ordinal))
            {
                tcpStartedTcs.TrySetResult(true);
            }

            if (e.Data.Contains(ViewerHttpsCertUnavailableMarker, StringComparison.Ordinal) ||
                e.Data.Contains(ViewerHttpsCertUnavailableEventId, StringComparison.Ordinal))
            {
                certUnavailableTcs.TrySetResult(true);
            }
        }

        _hostProcess.OutputDataReceived += OnData;
        _hostProcess.ErrorDataReceived += OnData;

        _hostProcess.Start();
        _hostProcess.BeginOutputReadLine();
        _hostProcess.BeginErrorReadLine();

        // 平文 UDP/TCP 受信は縮小継続の影響を受けず、通常どおり起動する（決定 2——受信を
        // 巻き添えにしない）。
        await WaitWithTimeoutAsync(udpStartedTcs.Task, StartupTimeout, "UDP リスナ起動ログ");
        await WaitWithTimeoutAsync(tcpStartedTcs.Task, StartupTimeout, "TCP リスナ起動ログ");

        // 証明書解決失敗による縮小継続の起動時警告（イベント ID 1035）が出る。
        await WaitWithTimeoutAsync(certUnavailableTcs.Task, StartupTimeout, "閲覧 HTTPS 証明書解決失敗（1035）の警告ログ");

        // 閲覧ポートには何も listen していない——HTTPS で開けないだけでなく、平文 HTTP へも
        // 落ちていない（決定 2「平文へは決して落ちない」の実プロセス検証）。Kestrel の bind は
        // WebApplication 構築時（= 上記の起動ログより前）に完了しているため、警告観測後の
        // 接続試行で bind の有無を確定できる。
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(_hostProcess.HasExited, "縮小継続で起動を継続するはずのプロセスが終了している。");

        using (var probe = new TcpClient())
        {
            var refused = false;
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, viewerPort, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            }
            catch (SocketException)
            {
                refused = true;
            }

            Assert.True(refused, $"閲覧ポート {viewerPort} への TCP 接続が成立した——縮小継続していない（平文または HTTPS で開いている）。");
        }

        _hostProcess.Kill(entireProcessTree: true);
        var exited = _hostProcess.WaitForExit((int)ShutdownTimeout.TotalMilliseconds);
        Assert.True(exited, "Yagura.Host の停止（Kill）がタイムアウトした。");
    }

    /// <summary>
    /// 空きポートを 1 つ予約してすぐ離す（<c>ListenerBindPlan.ResolvePortForDualStackLoopback</c> と
    /// 同じ「予約してから離す」手法。TOCTOU の狭い競合は残るがテスト用途に限られるため許容する）。
    /// </summary>
    private static int ReserveEphemeralPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
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
