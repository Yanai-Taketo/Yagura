using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Yagura.E2E.Tests;

/// <summary>
/// ADR-0010 Phase 2（リモートバインド解禁）の実プロセス回帰テスト。**Phase 2 受け入れ条件 (i)**
/// （ADR-0010 決定 8）——「認証なしでのリモートバインド拒否」の CI 回帰テストを、決定 4 の
/// HTTPS 前提条件も含めて固定する。既存 <see cref="AdminAuthenticationFailClosedRegressionTests"/>
/// （Phase 1 の loopback 認証 opt-in fail-closed）と同じ子プロセス起動パターンを踏襲する。
/// </summary>
/// <remarks>
/// <para>
/// <b>本クラスがカバーする項目</b>: ①認証・HTTPS のいずれか/両方が未構成のまま
/// <c>Admin:RemoteBinding:Enabled=true</c> にした場合の fail-closed 拒否（イベント ID 1012）、
/// ②認証・HTTPS が両方構成済み（自己署名テスト用証明書を LocalMachine\My ストアへ実際に導入）
/// の場合にリモート HTTPS ポートへ実際に到達でき、TLS 1.2 以上でネゴシエートすること、
/// ③証明書が解決できない場合（拇印は正しい形式だがストアに存在しない）の縮小継続——起動は
/// 中止せず、loopback 経由の管理リスナは影響を受けず、リモート HTTPS ポートのみ開かない
/// （イベント ID 1013）。
/// </para>
/// <para>
/// <b>証明書はテストごとに LocalMachine\My ストアへ実際に導入する</b>（configuration.md §6 の
/// 「Windows 証明書ストアからの参照を唯一の指定方式とする」設計を実体で検証するため、PFX
/// 直接指定のフェイクには頼らない）。テスト終了時にストアから除去する（ベストエフォート——
/// CNG の鍵コンテナファイル自体の削除までは行わない。使い捨て CI ランナーでの残留は実害が
/// 小さいと判断した）。
/// </para>
/// </remarks>
public sealed class AdminRemoteBindingRegressionTests : IDisposable
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private readonly List<string> _dataRoots = new();
    private readonly List<Process> _processes = new();
    private readonly List<string> _issuedThumbprints = new();

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
                // ベストエフォート（既存 E2E テストと同じ判断）。
            }
        }

        RemoveIssuedTestCertificates();
    }

    // ------------------------------------------------------------------
    // fail-closed 拒否（決定 1・4）
    // ------------------------------------------------------------------

    [Fact]
    public async Task RemoteBinding_WithNeitherAuthNorHttps_ProcessExitsNonZero_WithGuidanceMessageAndEventId()
    {
        var (exitCode, output) = await RunHostProcessToExitAsync("""
            {
              "Admin": { "RemoteBinding": { "Enabled": "true" } }
            }
            """);

        Assert.NotEqual(0, exitCode);
        // 日本語の本文は E2E テストでは照合しない(リダイレクトされた子プロセス stdout の
        // コードページ次第で化け得るため——AdminAuthenticationFailClosedRegressionTests と
        // 同じ判断。ロケール非依存の ASCII トークン(設定キー名・イベント ID)のみ照合する)。
        Assert.Contains("Admin:RemoteBinding:Enabled", output, StringComparison.Ordinal);
        Assert.Contains("Admin:Authentication:Windows:Enabled", output, StringComparison.Ordinal);
        Assert.Contains("Admin:Https:Enabled", output, StringComparison.Ordinal);
        Assert.Contains("[1012]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening on:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteBinding_WithAuthOnly_NoHttps_ProcessExitsNonZero()
    {
        // 決定 4「HTTPS 未構成のままリモートバインドを試みる設定は fail-closed で拒否する」——
        // 認証だけを構成しても HTTPS が欠ければ依然として拒否される。
        var (exitCode, output) = await RunHostProcessToExitAsync("""
            {
              "Admin": {
                "RemoteBinding": { "Enabled": "true" },
                "Authentication": { "Windows": { "Enabled": "true" } }
              }
            }
            """);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Admin:Https:Enabled", output, StringComparison.Ordinal);
        Assert.Contains("[1012]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteBinding_WithHttpsOnly_NoAuth_ProcessExitsNonZero()
    {
        var (thumbprint, _) = IssueAndInstallTestCertificate(TimeSpan.FromDays(365));

        var (exitCode, output) = await RunHostProcessToExitAsync($$"""
            {
              "Admin": {
                "RemoteBinding": { "Enabled": "true" },
                "Https": { "Enabled": "true", "CertificateThumbprint": "{{thumbprint}}" }
              }
            }
            """);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Admin:Authentication:Windows:Enabled", output, StringComparison.Ordinal);
        Assert.Contains("[1012]", output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 正常系: 認証 + HTTPS が両方構成済みなら、リモート HTTPS ポートへ実際に到達できる
    // ------------------------------------------------------------------

    [Fact]
    public async Task RemoteBinding_WithAuthAndValidCertificate_RemoteHttpsPortAcceptsTls12OrHigher_AndLoopbackStillPlainHttp()
    {
        var (thumbprint, _) = IssueAndInstallTestCertificate(TimeSpan.FromDays(365));

        var (process, adminPort, adminHttpsPort) = await StartHostProcessAsync($$"""
            {
              "Admin": {
                "RemoteBinding": { "Enabled": "true" },
                "Authentication": { "Windows": { "Enabled": "true" } },
                "Https": { "Enabled": "true", "CertificateThumbprint": "{{thumbprint}}" }
              }
            }
            """);

        try
        {
            // --- loopback (Admin:HttpPort) は引き続き平文 HTTP のまま（決定 4: loopback 経由の
            //     管理リスナは HTTPS の対象外のまま残る） ---
            using (var httpClient = new HttpClient())
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await httpClient.GetAsync($"http://127.0.0.1:{adminPort}/admin/login", cts.Token);
                // 認証 opt-in の画面遷移詳細までは検証しない——平文 HTTP で応答が返ること
                // （TLS ハンドシェイクを要求されないこと）自体が本テストの主張。
                Assert.True((int)response.StatusCode < 500, $"loopback 経由の応答が異常: {response.StatusCode}");
            }

            // --- リモート HTTPS ポートは TLS 1.2 以上でネゴシエートし、有効な HTTPS 応答を返す ---
            await AssertRemoteHttpsPortAcceptsConnectionAsync(adminHttpsPort);
        }
        finally
        {
            KillAndWait(process);
        }
    }

    [Fact]
    public async Task RemoteBinding_WithRequireForLoopbackFalse_LoopbackStaysUnauthenticated_ButRemoteRequiresAuthentication()
    {
        // ADR-0010 Phase 2 決定 1 の核心: Admin:Authentication:RequireForLoopback を既定
        // （false）のまま Admin:RemoteBinding:Enabled を有効化した、最も典型的な Phase 2 構成。
        // loopback 経由の管理操作は既定どおり無認証のまま到達できる一方、リモート HTTPS 経由の
        // 管理操作は（RequireForLoopback の値に関わらず）常に認証を要求されることを実機で確認する
        // （AdminAuthenticationExtensions.IsUnauthenticatedLoopbackBypassAllowed /
        // AdminScreenAccessPolicy.IsAuthenticationSatisfied の実装が実際に機能することの検証——
        // 単体テストでの真理値表確認だけでなく、実プロセスに対する実 HTTP 要求で固定する）。
        var (thumbprint, _) = IssueAndInstallTestCertificate(TimeSpan.FromDays(365));

        var (process, adminPort, adminHttpsPort) = await StartHostProcessAsync($$"""
            {
              "Admin": {
                "RemoteBinding": { "Enabled": "true" },
                "Authentication": { "App": { "Enabled": "true" } },
                "Https": { "Enabled": "true", "CertificateThumbprint": "{{thumbprint}}" }
              }
            }
            """);

        try
        {
            // フォワーダキットのダウンロードエンドポイント（素の minimal API。AdminPolicyName の
            // RequireAuthorization 対象——YaguraAdminExtensions.MapForwarderKitDownload）を使う。
            // 未認証時の応答は Cookie 認証スキームの既定挙動（LoginPath への 302 リダイレクト）。
            const string path = "/admin/forwarder-kit/download";

            // --- loopback: 無認証のまま到達できる（認証を要求されない = 302 リダイレクトされない） ---
            using (var loopbackClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await loopbackClient.GetAsync($"http://127.0.0.1:{adminPort}{path}", cts.Token);

                Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
                Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);
                Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            // --- リモート（HTTPS）: 未認証だと認証へリダイレクトされる（管理操作を実行できない） ---
            using (var socketsHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            })
            using (var remoteClient = new HttpClient(socketsHandler))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await remoteClient.GetAsync($"https://127.0.0.1:{adminHttpsPort}{path}", cts.Token);

                Assert.True(
                    response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.Unauthorized,
                    $"リモート経由の未認証アクセスが認証を要求されなかった（実際の応答: {response.StatusCode}）。" +
                    "ADR-0010 Phase 2 決定 1 の「リモート経由の管理操作は常に認証必須」不変条件違反。");

                if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found)
                {
                    Assert.NotNull(response.Headers.Location);
                    Assert.Contains("/admin/login", response.Headers.Location!.ToString(), StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            KillAndWait(process);
        }
    }

    // ------------------------------------------------------------------
    // 証明書が解決できない場合の縮小継続（決定 4。configuration.md §4.1 と同型の扱い）
    // ------------------------------------------------------------------

    [Fact]
    public async Task RemoteBinding_WithWellFormedButNonexistentThumbprint_StartsNormally_RemotePortNeverListens_LoopbackUnaffected()
    {
        // 拇印としては正しい形式（16 進 40 桁）だが、ストアに存在しない証明書を指す
        // （静的な設定検証は通過するが、実際の証明書ストア参照が失敗する環境依存シナリオ）。
        var nonexistentThumbprint = new string('F', 40);

        var (process, adminPort, output) = await StartHostProcessExpectingDegradedRemoteAsync($$"""
            {
              "Admin": {
                "RemoteBinding": { "Enabled": "true" },
                "Authentication": { "Windows": { "Enabled": "true" } },
                "Https": { "Enabled": "true", "CertificateThumbprint": "{{nonexistentThumbprint}}" }
              }
            }
            """);

        try
        {
            // 起動は中止しない（fail-closed の対象外——環境依存の縮小継続）。
            Assert.True(adminPort > 0);

            // 起動時警告（イベント ID 1013）が出力されている。
            Assert.Contains("[1013]", output, StringComparison.Ordinal);
            Assert.Contains("admin-https-certificate-unavailable", output, StringComparison.Ordinal);

            // loopback 経由の管理リスナは引き続き到達できる。
            using var httpClient = new HttpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await httpClient.GetAsync($"http://127.0.0.1:{adminPort}/admin/login", cts.Token);
            Assert.True((int)response.StatusCode < 500, $"loopback 経由の応答が異常: {response.StatusCode}");
        }
        finally
        {
            KillAndWait(process);
        }
    }

    // ------------------------------------------------------------------
    // TLS 接続検証
    // ------------------------------------------------------------------

    /// <summary>
    /// リモート HTTPS ポートへ TCP 接続し、TLS ハンドシェイクが成立すること・ネゴシエートされた
    /// プロトコルが TLS 1.2 以上であることを確認する（ADR-0010 Phase 2 決定 4「最低 TLS 1.2 以上を
    /// 最低要件とし、TLS 1.3 を優先する」）。証明書検証は自己署名のため無条件で許可する
    /// （本テストの主張は TLS プロトコルバージョンの下限であり、証明書チェーンの信頼性ではない）。
    /// </summary>
    private static async Task AssertRemoteHttpsPortAcceptsConnectionAsync(int adminHttpsPort)
    {
        using var tcpClient = new TcpClient();
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await tcpClient.ConnectAsync(IPAddress.Loopback, adminHttpsPort, connectCts.Token);

        using var sslStream = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, _, _, _) => true);

        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "127.0.0.1",
            // EnabledSslProtocols を明示しない（既定 = OS ネゴシエーションに委ねる）。実務上の
            // 現行クライアント（.NET 既定・現行ブラウザ）は TLS 1.0/1.1 を既定で提示しないため、
            // この既定挙動そのものが「サーバが TLS 1.2 以上を要求しても現行クライアントは
            // 支障なく接続できる」ことの現実的な検証になる。
        }, handshakeCts.Token);

        Assert.True(
            sslStream.SslProtocol is System.Security.Authentication.SslProtocols.Tls12
                or System.Security.Authentication.SslProtocols.Tls13,
            $"ネゴシエートされた TLS バージョンが下限（TLS 1.2）を満たさない: {sslStream.SslProtocol}");

        // 簡単な HTTP/1.1 要求を送り、Kestrel が TLS の内側で通常どおり応答することを確認する
        // （TLS ハンドシェイクの成立だけでなく、実際のアプリケーション層の疎通も確認する）。
        var request = Encoding.ASCII.GetBytes("GET /admin/login HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await sslStream.WriteAsync(request);

        using var reader = new StreamReader(sslStream, Encoding.ASCII, leaveOpen: true);
        var statusLine = await reader.ReadLineAsync(handshakeCts.Token);
        Assert.NotNull(statusLine);
        Assert.StartsWith("HTTP/1.1 ", statusLine, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // テスト用証明書の発行・導入（LocalMachine\My ストアへ実際に導入する）
    // ------------------------------------------------------------------

    /// <summary>
    /// 自己署名のテスト用証明書を発行し、LocalMachine\My ストアへ実際に導入する
    /// （configuration.md §6 の「Windows 証明書ストアからの参照を唯一の指定方式とする」設計を
    /// 実体で検証するため）。管理者権限を要する（本 E2E テストの実行環境は Administrator 前提）。
    /// </summary>
    private (string Thumbprint, X509Certificate2 Certificate) IssueAndInstallTestCertificate(TimeSpan validityDuration)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=yagura-e2e-admin-https-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.Add(validityDuration);

        using var ephemeralCertificate = request.CreateSelfSigned(notBefore, notAfter);

        // ストアへ導入するには、鍵をエフェメラル（メモリのみ）から永続化表現へ持ち替える必要がある
        // （CreateSelfSigned が返す証明書はプロセス終了で鍵が失われるエフェメラルキー）。
        var pfxBytes = ephemeralCertificate.Export(X509ContentType.Pfx);
        var persistableCertificate = X509CertificateLoader.LoadPkcs12(
            pfxBytes,
            password: null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persistableCertificate);
        store.Close();

        _issuedThumbprints.Add(persistableCertificate.Thumbprint);

        return (persistableCertificate.Thumbprint, persistableCertificate);
    }

    private void RemoveIssuedTestCertificates()
    {
        if (_issuedThumbprints.Count == 0)
        {
            return;
        }

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);

            foreach (var thumbprint in _issuedThumbprints)
            {
                var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                if (matches.Count > 0)
                {
                    store.Remove(matches[0]);
                }
            }

            store.Close();
        }
        catch (CryptographicException)
        {
            // ベストエフォート（クラスの remarks 参照）。
        }
    }

    // ------------------------------------------------------------------
    // 共通ヘルパー: 実プロセス起動（AdminAuthenticationFailClosedRegressionTests と同型パターン）。
    // ------------------------------------------------------------------

    private async Task<(int ExitCode, string Output)> RunHostProcessToExitAsync(string configJson)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-remotebind-{Guid.NewGuid():N}");
        _dataRoots.Add(dataRoot);
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "yagura.json"), configJson);

        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Yagura.Host.dll");
        Assert.True(File.Exists(hostDllPath), $"Yagura.Host.dll が見つからない: {hostDllPath}");

        var startInfo = BuildStartInfo(hostDllPath, dataRoot);

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
                $"fail-closed 拒否を期待したが、プロセスが {ExitTimeout} 以内に終了しなかった。出力:\n{output}");
        }

        lock (gate)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    private async Task<(Process Process, int AdminPort, int AdminHttpsPort)> StartHostProcessAsync(string configJson)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-remotebind-{Guid.NewGuid():N}");
        _dataRoots.Add(dataRoot);
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "yagura.json"), configJson);

        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Yagura.Host.dll");
        var startInfo = BuildStartInfo(hostDllPath, dataRoot);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _processes.Add(process);

        var adminPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adminHttpsPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adminPattern = new Regex(@"Now listening on:\s*http://127\.0\.0\.1:(\d+)\s*$");
        // リモート HTTPS エントリは AnyIP bind + UseHttps のため、Kestrel は "Now listening on:"
        // の URL スキームを https:// として出力する（実機確認: http://[::]:PORT ではなく
        // https://[::]:PORT）——閲覧リスナ（平文 http://[::]:PORT）と URL スキームで判別できる。
        var adminHttpsPattern = new Regex(@"Now listening on:\s*https://\[::\]:(\d+)\s*$");

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            var adminMatch = adminPattern.Match(e.Data);
            if (adminMatch.Success && int.TryParse(adminMatch.Groups[1].Value, out var port))
            {
                adminPortTcs.TrySetResult(port);
            }

            var adminHttpsMatch = adminHttpsPattern.Match(e.Data);
            if (adminHttpsMatch.Success && int.TryParse(adminHttpsMatch.Groups[1].Value, out var httpsPort))
            {
                adminHttpsPortTcs.TrySetResult(httpsPort);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(StartupTimeout);
        cts.Token.Register(() =>
        {
            adminPortTcs.TrySetCanceled();
            adminHttpsPortTcs.TrySetCanceled();
        });

        var adminPort = await adminPortTcs.Task;
        var adminHttpsPort = await adminHttpsPortTcs.Task;

        return (process, adminPort, adminHttpsPort);
    }

    /// <summary>
    /// 証明書が解決できず、リモート HTTPS bind エントリのみが縮小継続でスキップされる
    /// （プロセス自体は起動する）シナリオ用の起動ヘルパー。<c>Now listening on: http://[::]:</c>
    /// 行が最後まで現れないことを起動完了の判断材料にはできないため（そもそも出ない）、
    /// loopback の起動確認 + 一定時間の追加待機で「その後リモートポートの listen ログが
    /// 現れないこと」を確認する。
    /// </summary>
    private async Task<(Process Process, int AdminPort, string Output)> StartHostProcessExpectingDegradedRemoteAsync(string configJson)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-e2e-remotebind-{Guid.NewGuid():N}");
        _dataRoots.Add(dataRoot);
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "yagura.json"), configJson);

        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Yagura.Host.dll");
        var startInfo = BuildStartInfo(hostDllPath, dataRoot);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _processes.Add(process);

        var adminPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var warningTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adminPattern = new Regex(@"Now listening on:\s*http://127\.0\.0\.1:(\d+)\s*$");

        var output = new StringBuilder();
        var gate = new object();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (gate)
            {
                output.AppendLine(e.Data);
            }

            var adminMatch = adminPattern.Match(e.Data);
            if (adminMatch.Success && int.TryParse(adminMatch.Groups[1].Value, out var port))
            {
                adminPortTcs.TrySetResult(port);
            }

            if (e.Data.Contains("[1013]", StringComparison.Ordinal))
            {
                warningTcs.TrySetResult();
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(StartupTimeout);
        cts.Token.Register(() =>
        {
            adminPortTcs.TrySetCanceled();
            warningTcs.TrySetCanceled();
        });

        var adminPort = await adminPortTcs.Task;
        await warningTcs.Task;

        // 警告出力後、リモート bind の listen ログが追加で現れないことを確認するための猶予
        // （縮小継続の確認——非常に短時間の待機で十分。listen 自体は起動シーケンス内で即時に
        // 判断されるため、後から遅れて listen が始まることはない）。
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        string outputSnapshot;
        lock (gate)
        {
            outputSnapshot = output.ToString();
        }

        // 閲覧リスナ自体は既定（Lan）で AnyIP bind のため "Now listening on: http://[::]:PORT"
        // （平文）は正常に現れる——ここで否定するのは admin リモート HTTPS 固有の
        // "https://[::]:PORT" 行のみ（縮小継続によりこのエントリだけが bind されないことの確認）。
        Assert.DoesNotContain("https://[::]:", outputSnapshot, StringComparison.Ordinal);

        return (process, adminPort, outputSnapshot);
    }

    private static ProcessStartInfo BuildStartInfo(string hostDllPath, string dataRoot)
    {
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
        startInfo.Environment["YAGURA_ADMIN_HTTPS_PORT"] = "0";
        return startInfo;
    }

    private static void KillAndWait(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds);
    }
}
