using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Yagura.E2E.Tests;

/// <summary>
/// ゼロ設定ファーストラン（ADR-0006 基準 1 の導入体験の原則）の E2E smoke テスト。
/// </summary>
/// <remarks>
/// 実際の Yagura.Host 実行ファイルを子プロセスとして起動し、設定ファイル・手編集なしで
/// 「UDP でメッセージを送る → HTTP の閲覧ページに現れる」までを検証する。
/// 単体テスト・結合テストとは異なり、実バイナリの起動から検証する（M2-3 の受け入れ条件）。
/// </remarks>
public sealed class ZeroConfigFirstRunE2ETests : IDisposable
{
    private const string ListeningOnPrefix = "Now listening on:";
    private const string UdpListenerLogPrefix = "UDP syslog listener started on port";

    // M6-1(Issue #51)以降、Kestrel は複数アドレスへ bind するため「Now listening on:」行が
    // 3 本(閲覧 1 本 + 管理 2 本)出る。閲覧リスナは既定(Viewer:PublicAccess = Lan)で
    // ListenAnyIP を使う——実機確認済み(2026-07-05)で Kestrel は "http://[::]:{port}" と
    // ログに出す(0.0.0.0 ではなく IPv6 ワイルドカード表記)。管理リスナは常に loopback の
    // 具体アドレス("127.0.0.1:" / "[::1]:")でログに出るため、ワイルドカード表記の行だけを
    // 閲覧リスナのものとして識別できる。
    private static readonly Regex ViewerListeningPortPattern =
        new(@"Now listening on:\s*http://\[::\]:(\d+)\s*$", RegexOptions.Compiled);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HttpPollTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-{Guid.NewGuid():N}");
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
                // ベストエフォート。子プロセス停止直後は SQLite の WAL 補助ファイルの
                // ハンドル解放に短い遅延があり得るが、テスト環境の一時ディレクトリの
                // 残留は致命的でないため、削除失敗で本テストを失敗させない。
            }
        }
    }

    [Fact]
    public async Task ZeroConfigFirstRun_UdpMessage_AppearsOnHttpViewerPage()
    {
        Directory.CreateDirectory(_dataRoot);

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

        // ゼロ設定ファーストラン: 設定ファイル・手編集を一切行わず、環境変数のみで
        // 一時データルート・OS 採番ポート（HTTP/UDP/TCP とも 0）を指定する。
        // TCP は M4-1 で追加。既定ポート 514 は多くの環境で管理者権限を要するため、
        // 0 を指定しないと bind 失敗で起動が止まる（本テストは非管理者実行を前提とする）。
        startInfo.Environment["YAGURA_DATAROOT"] = _dataRoot;
        startInfo.Environment["YAGURA_HTTP_PORT"] = "0";
        startInfo.Environment["YAGURA_UDP_PORT"] = "0";
        startInfo.Environment["YAGURA_TCP_PORT"] = "0";
        // 管理リスナ(M6-1)も OS 採番にする——閲覧・受信ポートと同じ流儀。指定しないと
        // 既定の 8515 に固定され、CI 上での並列実行や他プロセスとのポート衝突を招き得る。
        startInfo.Environment["YAGURA_ADMIN_PORT"] = "0";

        _hostProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdoutLines = new List<string>();
        var stdoutLock = new object();
        var viewerPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var udpPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

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

            // M6-1(Issue #51)以降、閲覧リスナは既定(Lan)で全インターフェース bind
            // (ListenAnyIP)のため、Kestrel は "http://[::]:{port}" とログに出す
            // （ワイルドカードアドレスは接続先として使えないため、127.0.0.1 へ接続する
            // 実アドレスはポート番号のみここから取り出し、下で組み立てる）。
            var viewerMatch = ViewerListeningPortPattern.Match(e.Data);
            if (viewerMatch.Success && int.TryParse(viewerMatch.Groups[1].Value, out var viewerPort))
            {
                viewerPortTcs.TrySetResult(viewerPort);
            }
        };

        _hostProcess.Start();
        _hostProcess.BeginOutputReadLine();
        _hostProcess.BeginErrorReadLine();

        var udpPort = await WaitWithTimeoutAsync(udpPortTcs.Task, StartupTimeout, "UDP リスナ起動ログ");
        var viewerPort = await WaitWithTimeoutAsync(viewerPortTcs.Task, StartupTimeout, "閲覧リスナ HTTP listening ログ");

        // architecture.md §1.2 起動順序の実証: 「UDP syslog listener started」のログ行が
        // 「Now listening on:」より先に stdout へ現れること（受信開始が Kestrel の listen
        // 開始より先行する）。IHostedService.StartAsync が Web サーバの起動より先に
        // 完了まで待たれる規約（Microsoft Learn "Background tasks with hosted services in
        // ASP.NET Core" の "IHostedService interface" > "StartAsync" 節: "StartAsync is
        // called before: The app's request processing pipeline is configured. The server
        // is started and IApplicationLifetime.ApplicationStarted is triggered." の記載。
        // 確認日 2026-07-05）の帰結を、実ログ順で確認する。
        int udpLogLineIndex;
        int listeningLogLineIndex;
        lock (stdoutLock)
        {
            udpLogLineIndex = stdoutLines.FindIndex(l => l.Contains(UdpListenerLogPrefix, StringComparison.Ordinal));
            listeningLogLineIndex = stdoutLines.FindIndex(l => l.Contains(ListeningOnPrefix, StringComparison.Ordinal));
        }

        Assert.True(udpLogLineIndex >= 0, "UDP リスナ起動ログが見つからない。");
        Assert.True(listeningLogLineIndex >= 0, "HTTP listening ログが見つからない。");
        Assert.True(
            udpLogLineIndex < listeningLogLineIndex,
            $"起動順序が期待と異なる（受信先行のはずが逆転）。UDP行={udpLogLineIndex}, HTTP行={listeningLogLineIndex}");

        // UDP で syslog テストメッセージを送信する。
        var marker = $"e2e-smoke-{Guid.NewGuid():N}";
        using (var udpClient = new UdpClient())
        {
            var payload = Encoding.UTF8.GetBytes($"<134>hello from e2e: {marker}");
            await udpClient.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, udpPort));
        }

        // ワイルドカードアドレス([::])は接続先として使えないため、127.0.0.1 へ接続する
        // (閲覧リスナは既定で LAN 公開だが、ループバックからの接続も当然受け付ける)。
        using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{viewerPort}") };

        var pageContainsMarker = await PollUntilAsync(
            async () =>
            {
                var html = await httpClient.GetStringAsync("/");
                return html.Contains(marker, StringComparison.Ordinal);
            },
            HttpPollTimeout);

        Assert.True(pageContainsMarker, "送信した syslog メッセージが閲覧ページの HTML に現れなかった。");

        // プロセスの停止。判断の詳細は本メソッド末尾のコメントを参照。
        _hostProcess.Kill(entireProcessTree: true);
        var exited = _hostProcess.WaitForExit((int)ShutdownTimeout.TotalMilliseconds);
        Assert.True(exited, "Yagura.Host の停止（Kill）がタイムアウトした。");

        // 判断: グレースフル停止（SIGTERM 相当）は Windows コンソールプロセスには
        // 存在しない。.NET の Generic Host は Ctrl+C/Ctrl+Break のコンソールイベントで
        // 正常停止をトリガできるが、別プロセスから特定の子プロセスにのみ Ctrl+Break を
        // 送るには Win32 の GenerateConsoleCtrlEvent + CREATE_NEW_PROCESS_GROUP の組が
        // 必要であり、.NET の Process/ProcessStartInfo はこの生成フラグを公開 API として
        // 提供していない（P/Invoke で CreateProcess を直接呼ぶ実装が必要になる）。
        // 標準出力の読み取り・環境変数注入を伴う本テストの構成でそれを安定実装する
        // コストは、smoke テスト 1 本の価値に見合わないと判断し、本テストでは
        // Process.Kill(entireProcessTree: true) を採用した（「可能なら SIGTERM 相当、
        // 無理なら Kill でよい」という設計依頼のとおり）。M4 以降で停止順序
        // （architecture.md §1.3 スプール退避等）を検証する専用テストを追加する際に、
        // グレースフル停止の P/Invoke 実装を再検討する。
    }

    /// <summary>
    /// テストプロジェクト自身の出力ディレクトリに ProjectReference 経由でコピーされた
    /// Yagura.Host.dll のパスを解決する（Yagura.E2E.Tests.csproj のコメント参照）。
    /// </summary>
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

    /// <summary>
    /// 条件ポーリング（固定 sleep ではなく上限付きで繰り返し確認する。conventions.md の
    /// 時間窓の扱いに準ずる——CI 環境の揺らぎに対して安定させるため）。
    /// </summary>
    private static async Task<bool> PollUntilAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await probe())
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // サーバがまだ listen を完了していない初回アクセス時等の一過性失敗は無視して再試行する。
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        return await probe();
    }
}
