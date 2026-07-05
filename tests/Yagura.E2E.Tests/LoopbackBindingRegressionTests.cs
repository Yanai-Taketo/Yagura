using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.RegularExpressions;

namespace Yagura.E2E.Tests;

/// <summary>
/// loopback 束縛の CI 回帰テスト L-1〜L-4（M6-3。Issue #53。security.md §1）の実プロセス
/// 統合テスト。**v0.1 の受け入れ条件**（ADR-0004 決定 2）。
/// </summary>
/// <remarks>
/// <para>
/// <b>本クラスがカバーする項目</b>: L-1（管理リスナの bind 先検証）・L-2（WebSocket 昇格後の
/// 維持）・L-3a（非 loopback からの到達不可）・L-4（設定破損時の不変条件）。
/// </para>
/// <para>
/// <b>L-3b はカバーしない（重複実装しない）</b>: 「閲覧リスナに到達した管理系要求の拒否 +
/// 監査記録」は <see cref="ListenerGuardAuditE2ETests"/>
/// （<c>ViewerPort_AdminRequest_ReturnsNotFound_AndLeavesOneAuditLine</c>）が既に検証している
/// （M6-2。Issue #52）。security.md §1 の表と 1 対 1 に対応させるため、本クラスからは実装せず
/// この参照コメントのみを置く。
/// </para>
/// <para>
/// <b>L-1 の再利用</b>: <see cref="ListenerSeparationE2ETests"/>（M6-1）は「管理ポートが
/// 閲覧ポートと異なり、管理系エンドポイントへ到達できる」ことを stdout ログの正規表現一致で
/// 確認しているが、「閲覧・管理以外のアドレス（0.0.0.0・実インターフェースアドレス）に
/// 管理ポートが束縛されていないこと」の**網羅的**な検証（stdout の全 "Now listening on:" 行の
/// 走査 + OS レベルの <see cref="IPGlobalProperties.GetActiveTcpListeners"/> 照合）までは
/// 行っていない。本クラスの L-1 はその網羅検証を独立して行う（重複ではなく検証粒度の拡張）。
/// </para>
/// <para>
/// <b>実プロセス起動の相乗り</b>: L-1・L-2・L-3a は同一プロセスに相乗りさせ（
/// <see cref="L1_L2_L3a_AdminListener_BindsToLoopbackOnly_AndSurvivesWebSocketUpgrade_AndRejectsNonLoopbackSource"/>）、
/// L-4 のみ設定破損シナリオごとに個別プロセスを起動する（シナリオ間で設定内容そのものが
/// 異なるため相乗りできない）。
/// </para>
/// </remarks>
public sealed class LoopbackBindingRegressionTests : IDisposable
{
    private const string UdpListenerLogPrefix = "UDP syslog listener started on port";
    private const string ListeningOnPrefix = "Now listening on:";

    private static readonly Regex ListeningOnLinePattern =
        new(@"Now listening on:\s*(\S+)\s*$", RegexOptions.Compiled);

    // 管理リスナは常に loopback の具体アドレスでログに出る（127.0.0.1 と [::1] の 2 行。
    // ListenerBindPlan の ResolvePortForDualStackLoopback により両方とも同じポートになる）。
    // Kestrel は bind 順（閲覧 → 管理 IPv4 → 管理 IPv6）に "Now listening on:" を出すため、
    // 起動完了の待機は最後に出る [::1] 行まで行う——127.0.0.1 行の検出だけで先へ進むと、
    // [::1] 行が stdout コールバックに届く前に L-1 の行数検証が走る競合があり得る。
    private static readonly Regex AdminListeningPortPattern =
        new(@"Now listening on:\s*http://127\.0\.0\.1:(\d+)\s*$", RegexOptions.Compiled);

    private static readonly Regex AdminV6ListeningPortPattern =
        new(@"Now listening on:\s*http://\[::1\]:(\d+)\s*$", RegexOptions.Compiled);

    private static readonly Regex ViewerListeningPortPattern =
        new(@"Now listening on:\s*http://\[::\]:(\d+)\s*$", RegexOptions.Compiled);

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly List<string> _dataRoots = new();
    private readonly List<Process> _hostProcesses = new();

    public void Dispose()
    {
        foreach (var process in _hostProcesses)
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }

        foreach (var dataRoot in _dataRoots)
        {
            if (!Directory.Exists(dataRoot))
            {
                continue;
            }

            try
            {
                Directory.Delete(dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // ベストエフォート（他 E2E テストと同じ判断）。
            }
        }
    }

    // ------------------------------------------------------------------
    // L-1 / L-2 / L-3a: 単一プロセスに相乗り。
    // ------------------------------------------------------------------

    [Fact]
    public async Task L1_L2_L3a_AdminListener_BindsToLoopbackOnly_AndSurvivesWebSocketUpgrade_AndRejectsNonLoopbackSource()
    {
        var stdoutLines = new List<string>();
        var stdoutLock = new object();

        var (process, adminPort, _) = await StartHostProcessAsync(
            onStdoutLine: line =>
            {
                lock (stdoutLock)
                {
                    stdoutLines.Add(line);
                }
            });

        try
        {
            // --- L-1: 管理リスナが 127.0.0.1 と [::1] の 2 行のみで stdout に現れる ---
            List<string> listeningLinesSnapshot;
            lock (stdoutLock)
            {
                listeningLinesSnapshot = new List<string>(stdoutLines);
            }

            AssertAdminPortAppearsOnlyOnLoopbackLines(listeningLinesSnapshot, adminPort);

            // --- L-1: OS レベル検証（起動直後） ---
            AssertOsLevelListenersAreLoopbackOnly(adminPort);

            // --- L-2: /_blazor への WebSocket 昇格が管理ポートで成立することを確認する ---
            //
            // Blazor の完全な SignalR ネゴシエーションプロトコルを喋る必要はない
            // （security.md §1 L-2 の要求は「同一ポートで WebSocket ハンドシェイクが成立する
            // こと」であり、Blazor circuit として意味のある通信が続くことまでは求めない）。
            // ただし SignalR は「まず POST /_blazor/negotiate で connectionToken を取得し、
            // その値を id クエリパラメータとして渡した接続のみ WebSocket 昇格を受け付ける」
            // 仕様のため（実機確認済み。connectionId をそのまま渡すと 404
            // "No Connection with that ID" になる——2026-07-05 確認）、ネゴシエーション自体は
            // 省略できない（本テストが実施するのは HTTP ネゴシエーション 1 回のみで、
            // Blazor 固有のメッセージプロトコル・circuit 確立の中身までは喋らない）。
            // /_blazor は YaguraWebViewerExtensions.MapYaguraWebViewer が登録する閲覧系
            // ルートであり ListenerPortGuardEndpointMetadata.Admin を持たないため、管理リスナ
            // 経由でも到達できる設計になっている（Program.cs のコメント「管理リスナからの
            // 閲覧系到達を妨げる理由はない」参照）——本テストは管理ポート上の /_blazor に
            // 接続することで、Kestrel の同一リスナ内で HTTP から WebSocket への昇格が
            // 起こることを検証する。
            {
                var connectionToken = await PollUntilNegotiateSucceedsAsync(adminPort, TimeSpan.FromSeconds(15));
                var uri = new Uri($"ws://127.0.0.1:{adminPort}/_blazor?id={connectionToken}");

                // ネゴシエーションで払い出された connectionToken には短い有効期限があるため
                // （SignalR のコネクション租借の仕組み）、ConnectAsync 自体は 1 回のみ試みる
                // （直前の negotiate 成功でサーバが listen 済みであることは既に確認済みのため、
                // 「Kestrel が listen を完了する直前」の一過性失敗を再ポーリングする必要はない）。
                using var clientWebSocket = new ClientWebSocket();
                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await clientWebSocket.ConnectAsync(uri, connectCts.Token);

                Assert.Equal(WebSocketState.Open, clientWebSocket.State);

                clientWebSocket.Abort();
            }

            // --- L-2: 昇格後も listen 構成が増えていないこと（新しいポートで listen して
            // いないこと）を、L-1 の OS レベル検証を再実行して確認する ---
            AssertOsLevelListenersAreLoopbackOnly(adminPort);

            // --- L-3a: 非 loopback アドレスから管理ポートへ到達できないこと ---
            await AssertNonLoopbackSourceCannotReachAdminPortAsync(adminPort);
        }
        finally
        {
            KillAndWait(process);
        }
    }

    /// <summary>
    /// stdout の全 "Now listening on:" 行のうち、管理ポート番号を含む行が
    /// <c>http://127.0.0.1:{port}</c> と <c>http://[::1]:{port}</c> の 2 行のみであることを
    /// 確認する（0.0.0.0・実インターフェースアドレス・他ワイルドカード表記で管理ポートが
    /// 現れないこと）。
    /// </summary>
    private static void AssertAdminPortAppearsOnlyOnLoopbackLines(IReadOnlyList<string> stdoutLines, int adminPort)
    {
        var listeningLines = stdoutLines
            .Where(l => l.Contains(ListeningOnPrefix, StringComparison.Ordinal))
            .ToList();

        var linesWithAdminPort = new List<string>();
        foreach (var line in listeningLines)
        {
            var match = ListeningOnLinePattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var url = match.Groups[1].Value;
            if (TryExtractPort(url) == adminPort)
            {
                linesWithAdminPort.Add(line);
            }
        }

        Assert.True(
            linesWithAdminPort.Count == 2,
            $"管理ポート {adminPort} を含む \"Now listening on:\" 行が 2 行のはずが {linesWithAdminPort.Count} 行だった。" +
            $"行一覧: {string.Join(" | ", linesWithAdminPort)}");

        Assert.Contains(linesWithAdminPort, l => l.Contains($"http://127.0.0.1:{adminPort}", StringComparison.Ordinal));
        Assert.Contains(linesWithAdminPort, l => l.Contains($"http://[::1]:{adminPort}", StringComparison.Ordinal));

        // 明示的な否定: 0.0.0.0 / [::] / 実インターフェースアドレスでは絶対に現れない。
        Assert.DoesNotContain(linesWithAdminPort, l => l.Contains("0.0.0.0", StringComparison.Ordinal));
        Assert.DoesNotContain(linesWithAdminPort, l => l.Contains("http://[::]:", StringComparison.Ordinal));
    }

    private static int? TryExtractPort(string url)
    {
        var uri = new Uri(url);
        return uri.Port;
    }

    /// <summary>
    /// <see cref="IPGlobalProperties.GetActiveTcpListeners"/> を管理ポートでフィルタし、
    /// 全エントリのアドレスが loopback（IPv4 127.0.0.1 系または IPv6 ::1）であることを
    /// 確認する（他プロセスが同じポート番号を別アドレスで使う偶発的な衝突はあり得るが、
    /// ポートは OS 採番（YAGURA_ADMIN_PORT=0）で取得した実ポートのため、そのポートで
    /// listen しているのが本テストのプロセス以外であることは通常ない）。
    /// </summary>
    private static void AssertOsLevelListenersAreLoopbackOnly(int adminPort)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();

        var matchingListeners = listeners.Where(l => l.Port == adminPort).ToList();

        Assert.True(
            matchingListeners.Count > 0,
            $"OS の TCP listener 一覧に管理ポート {adminPort} が見つからない。");

        foreach (var listener in matchingListeners)
        {
            Assert.True(
                IPAddress.IsLoopback(listener.Address),
                $"管理ポート {adminPort} が loopback 以外のアドレス {listener.Address} で listen している。");
        }

        // L-1 の合格条件は「127.0.0.1 と ::1 のみ」であり、「loopback のみ」に加えて
        // 「両系統が存在する」ことも含む(片系統の bind が黙って失われた退行を捕まえる。
        // L-4 はこの検証を再利用するため、破損設定下でも両系統が揃うことまで確認される)。
        Assert.True(
            matchingListeners.Any(l => l.Address.Equals(IPAddress.Loopback)),
            $"管理ポート {adminPort} の IPv4 loopback (127.0.0.1) の listen が存在しない。");
        Assert.True(
            matchingListeners.Any(l => l.Address.Equals(IPAddress.IPv6Loopback)),
            $"管理ポート {adminPort} の IPv6 loopback (::1) の listen が存在しない。");
    }

    /// <summary>
    /// マシンの非 loopback アドレス（実インターフェースの IPv4）を列挙し、そのアドレスの
    /// 管理ポートへの TCP 接続が確立しないことを確認する。
    /// </summary>
    /// <remarks>
    /// 非 loopback アドレスが 1 つもない環境ではスキップする——ただし CI 上
    /// （環境変数 <c>CI=true</c>。GitHub Actions の既定環境変数）ではスキップを許さず
    /// <see cref="Assert.Fail(string)"/> にする（偽 green 防止。M5 の SQL Server 適合テストと
    /// 同じ規約——<c>SqlServerLogStoreConformanceTests.IsRunningInCi</c> 参照）。CI ランナー
    /// （windows-latest）には必ず実インターフェース（少なくとも 1 つの非 loopback IPv4）が
    /// 存在するため、この分岐が CI で実際に踏まれることはない想定。
    /// </remarks>
    private static async Task AssertNonLoopbackSourceCannotReachAdminPortAsync(int adminPort)
    {
        var nonLoopbackAddress = TryGetNonLoopbackIPv4Address();

        if (nonLoopbackAddress is null)
        {
            if (IsRunningInCi())
            {
                Assert.Fail(
                    "L-3a: 非 loopback の IPv4 アドレスが見つからないため検証をスキップしようとしたが、" +
                    "CI 環境ではスキップを許さない（偽 green 防止）。CI ランナーの構成を確認すること。");
            }

            return;
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var connectTask = socket.ConnectAsync(new IPEndPoint(nonLoopbackAddress, adminPort), connectCts.Token).AsTask();

        try
        {
            await connectTask;

            // 接続が確立してしまった場合は明確な失敗（管理ポートが非 loopback アドレスからも
            // 到達可能になっている——L-3a の不変条件違反）。
            Assert.Fail(
                $"L-3a 違反: 非 loopback アドレス {nonLoopbackAddress} から管理ポート {adminPort} への " +
                "TCP 接続が確立してしまった。");
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode is SocketError.ConnectionRefused
                or SocketError.TimedOut
                or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable
                or SocketError.AddressNotAvailable)
        {
            // 期待どおり: OS レベルで拒否される（ConnectionRefused）か、応答がなくタイムアウトする。
            // これらはいずれも「確立しない」ことの証跡であり、L-3a の合格条件を満たす。
        }
        catch (OperationCanceledException)
        {
            // ConnectAsync 自体がキャンセルされた場合（タイムアウト）も「確立しない」の証跡。
        }
    }

    /// <summary>
    /// このマシンの非 loopback ・非リンクローカルな IPv4 アドレスを 1 つ返す（見つからなければ
    /// <see langword="null"/>）。CI ランナー（windows-latest）には必ず実インターフェースの
    /// IPv4 アドレスが存在する。
    /// </summary>
    private static IPAddress? TryGetNonLoopbackIPv4Address()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var properties = networkInterface.GetIPProperties();
            foreach (var unicastAddress in properties.UnicastAddresses)
            {
                var address = unicastAddress.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (IPAddress.IsLoopback(address))
                {
                    continue;
                }

                return address;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // L-4: 設定破損時の不変条件。シナリオごとに個別プロセスを起動する。
    // ------------------------------------------------------------------

    [Fact]
    public async Task L4_CorruptViewerPublicAccess_AdminListener_StillBindsToLoopbackOnly()
    {
        // シナリオ (a): Viewer:PublicAccess に不正値。閲覧が縮小(LocalhostOnly)されても
        // 管理は常に loopback のまま(configuration.md §1「公開範囲・bind 先の不正値は
        // 製品既定へ落とさない」——ResolveViewerPublicAccess のコメント参照)。
        await RunL4ScenarioAsync(
            scenarioName: "Viewer:PublicAccess 不正値",
            configJson: """
                {
                  "Viewer": { "PublicAccess": "not-a-valid-value" }
                }
                """);
    }

    [Fact]
    public async Task L4_CorruptAdminHttpPortInFile_EnvironmentZeroWins_AdminListener_StillBindsToLoopbackOnly()
    {
        // シナリオ (b): Admin:HttpPort に不正値。設定ファイル側は不正値のままだが、
        // 環境変数 YAGURA_ADMIN_PORT=0（テストが常に注入する。StartHostProcessAsync 参照）が
        // 優先されるため、「設定ファイルの不正値」+「環境変数の正常な上書き」という組み合わせで
        // 「不正値でも起動し loopback のみで listen する」ことを検証する
        // （固定ポート衝突を避けつつ、ResolveAdminHttpPort の優先順位——環境変数 > ファイル値/既定値
        // ——が破損設定下でも安全側に働くことの確認）。
        await RunL4ScenarioAsync(
            scenarioName: "Admin:HttpPort 不正値（ファイル側）+ 環境変数 0 が優先",
            configJson: """
                {
                  "Admin": { "HttpPort": "not-a-port-number" }
                }
                """);
    }

    [Fact]
    public async Task L4_UnknownConfigurationKeys_AdminListener_StillBindsToLoopbackOnly()
    {
        // シナリオ (c): 未知キーを含む設定ファイル。YaguraConfigurationLoader.KnownKeys に
        // 無いキーは警告のみで無視される(DetectUnknownKeys)——管理リスナの bind 計算
        // (ListenerBindPlan.Create)には一切影響しないことを確認する。
        await RunL4ScenarioAsync(
            scenarioName: "未知キーを含む設定ファイル",
            configJson: """
                {
                  "TotallyUnknownSection": { "SomeKey": "some-value" },
                  "Admin": { "SomeNonExistentSubKey": "ignored" }
                }
                """);
    }

    [Fact]
    public async Task L4_AdminBindAddressKeyDoesNotExist_IsIgnored_AdminListener_StillBindsToLoopbackOnly()
    {
        // シナリオ (d): 管理リスナの bind 先を変えようとする「存在しないキー」
        // (例: Admin:BindAddress = 0.0.0.0)。YaguraConfigurationOptions にはそもそも
        // Admin.BindAddress というプロパティが存在しない(Admin.HttpPort のみ)ため、
        // Configuration.Bind は単に無視する。ListenerBindPlan.Create も設定値
        // (ViewerPublicAccess 等)を一切参照せず管理リスナの bind 先を常に loopback の
        // 両系統で直接構築するため(§1 の不変条件をコードの構造そのもので保証)、
        // このキーを書いても bind 先は変わらないことを確認する。
        await RunL4ScenarioAsync(
            scenarioName: "存在しない Admin:BindAddress キー",
            configJson: """
                {
                  "Admin": { "BindAddress": "0.0.0.0", "HttpPort": "8515" }
                }
                """);
    }

    /// <summary>
    /// 1 つの L-4 シナリオ（破損した yagura.json を注入したデータルートでプロセスを起動する）を
    /// 実行し、L-1 の OS レベル検証を再利用して管理リスナが loopback のみで listen することを
    /// 確認する。
    /// </summary>
    private async Task RunL4ScenarioAsync(string scenarioName, string configJson)
    {
        var (process, adminPort, dataRoot) = await StartHostProcessAsync(
            onStdoutLine: null,
            beforeStart: root =>
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "yagura.json"), configJson);
            });

        try
        {
            AssertOsLevelListenersAreLoopbackOnly(adminPort);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"L-4 シナリオ「{scenarioName}」（データルート: {dataRoot}）で管理リスナの " +
                $"loopback 束縛が破られた: {ex.Message}");
        }
        finally
        {
            KillAndWait(process);
        }
    }

    // ------------------------------------------------------------------
    // 共通ヘルパー: 実プロセス起動。
    // ------------------------------------------------------------------

    /// <summary>
    /// Yagura.Host を子プロセスとして起動し、UDP リスナ起動ログ・管理ポート番号を stdout から
    /// 取得するまで待機する。
    /// </summary>
    /// <param name="onStdoutLine">stdout の各行を通知するコールバック（不要なら <see langword="null"/>）。</param>
    /// <param name="beforeStart">プロセス起動前にデータルートへ設定ファイル等を書き込むための
    /// コールバック（データルートディレクトリの絶対パスを受け取る。L-4 のシナリオ注入に使う）。</param>
    private async Task<(Process Process, int AdminPort, string DataRoot)> StartHostProcessAsync(
        Action<string>? onStdoutLine,
        Action<string>? beforeStart = null)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-loopback-{Guid.NewGuid():N}");
        _dataRoots.Add(dataRoot);

        if (beforeStart is not null)
        {
            beforeStart(dataRoot);
        }
        else
        {
            Directory.CreateDirectory(dataRoot);
        }

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
        _hostProcesses.Add(process);

        var udpPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adminPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adminV6PortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            onStdoutLine?.Invoke(e.Data);

            if (e.Data.Contains(UdpListenerLogPrefix, StringComparison.Ordinal))
            {
                udpPortTcs.TrySetResult(0);
            }

            var adminMatch = AdminListeningPortPattern.Match(e.Data);
            if (adminMatch.Success && int.TryParse(adminMatch.Groups[1].Value, out var adminPort))
            {
                adminPortTcs.TrySetResult(adminPort);
            }

            var adminV6Match = AdminV6ListeningPortPattern.Match(e.Data);
            if (adminV6Match.Success && int.TryParse(adminV6Match.Groups[1].Value, out var adminV6Port))
            {
                adminV6PortTcs.TrySetResult(adminV6Port);
            }

            var viewerMatch = ViewerListeningPortPattern.Match(e.Data);
            if (viewerMatch.Success && int.TryParse(viewerMatch.Groups[1].Value, out var viewerPort))
            {
                viewerPortTcs.TrySetResult(viewerPort);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitWithTimeoutAsync(udpPortTcs.Task, StartupTimeout, "UDP リスナ起動ログ");
        var resolvedAdminPort = await WaitWithTimeoutAsync(adminPortTcs.Task, StartupTimeout, "管理リスナ HTTP listening ログ (127.0.0.1)");

        // Kestrel の "Now listening on:" は bind 順に出るため、最後に出る管理リスナ [::1] 行まで
        // 待つ(127.0.0.1 行の検出だけで先へ進むと、[::1] 行が届く前に L-1 の行数検証が走る
        // 競合があり得る——AdminV6ListeningPortPattern のコメント参照)。
        var resolvedAdminV6Port = await WaitWithTimeoutAsync(adminV6PortTcs.Task, StartupTimeout, "管理リスナ HTTP listening ログ ([::1])");
        Assert.Equal(resolvedAdminPort, resolvedAdminV6Port);

        // 閲覧リスナも起動完了まで待つ(公開範囲が LocalhostOnly に縮小された場合でも
        // "Now listening on:" ログ自体は出るため、ViewerListeningPortPattern が一致しない
        // 構成 (LocalhostOnly) では待機せずに先へ進む——L-4 シナリオ (a) は閲覧を縮小するため
        // ワイルドカードパターンが一致しないことを許容する)。
        await Task.WhenAny(viewerPortTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        return (process, resolvedAdminPort, dataRoot);
    }

    private static void KillAndWait(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        var exited = process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds);
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
    /// SignalR の HTTP ネゴシエーション（<c>POST /_blazor/negotiate</c>）をポーリングし、
    /// 成功した応答から <c>connectionToken</c> を取り出して返す。Kestrel が listen を完了する
    /// 直前のタイミングでの一過性失敗（接続拒否）を許容する（他 E2E テストの
    /// PollForResponseAsync と同じ判断）。
    /// </summary>
    /// <remarks>
    /// SignalR ネゴシエーション応答の JSON は英数字・記号のみで構成される
    /// （<c>connectionToken</c> は Base64url 相当の乱数文字列）ため、正規表現によるロケール
    /// 非依存の抽出で十分であり、System.Text.Json への依存を増やさない。
    /// </remarks>
    private static async Task<string> PollUntilNegotiateSucceedsAsync(int adminPort, TimeSpan timeout)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{adminPort}") };

        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await httpClient.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);
                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadAsStringAsync();
                var match = Regex.Match(body, "\"connectionToken\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }

                lastException = new InvalidOperationException(
                    $"ネゴシエーション応答に connectionToken が見つからなかった: {body}");
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException(
            $"SignalR ネゴシエーションがタイムアウトまでに成立しなかった。最後の例外: {lastException}");
    }

    /// <summary>
    /// GitHub Actions の既定環境変数 <c>CI</c>（確認日 2026-07-05）で CI 実行かどうかを判定する。
    /// <c>SqlServerLogStoreConformanceTests.IsRunningInCi</c> と同じ判断（偽 green 防止規約）。
    /// </summary>
    private static bool IsRunningInCi() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
}
