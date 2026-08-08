using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Yagura.Bench.HostProcess;
using Yagura.Bench.LoadGeneration;
using Yagura.Bench.Verification;
using Yagura.TestSupport;

namespace Yagura.Bench.Tests;

/// <summary>
/// ADR-0010 Phase 2 決定 8 受け入れ条件 (iv)「管理リスナへの認証負荷（大量のログイン試行・
/// 接続集中）が syslog 受信スループットに悪影響を及ぼさないこと」の負荷試験（Yagura.Bench 流用。
/// クラス自体は <c>tests/Yagura.Bench.Tests</c> に置く——<see cref="BenchSmokeTests"/> と同じ
/// 「実バイナリを子プロセスとして起動し、短時間で CI を回せる規模で検証する」流儀）。
/// </summary>
/// <remarks>
/// <para>
/// <b>測定方針</b>: 同一の稼働中プロセスに対して、①UDP 受信のみを行うベースライン区間、
/// ②同じ UDP 受信と**同時に**管理リスナへログイン試行（アプリ独自認証の
/// <c>/admin/login/app</c>）を継続的に送り続ける区間、の 2 区間を連続して計測し、
/// 「送信数 = 保存件数（+ アプリ内カウンタ）」の完全一致（受信ロス 0）が両区間で成立することを
/// 主張する。**タイミング（所要時間・スループットの絶対値）は参考情報として記録するに留め、
/// 厳密な閾値判定はしない**（CI ランナーの負荷変動によるフレーキーさを避けるため——
/// <see cref="BenchSmokeTests"/> が OS レベル統計差分を「参考情報」に留める判断と同じ流儀）。
/// **主張の核心は「ロスが増えないこと」**であり、これは architecture.md の「ロスは必ず計上する」
/// 原則が既に握っている不変条件（受信パイプラインの完全性）を、認証負荷という新しい攻撃対象面
/// （悪意ある大量ログイン試行を含む）が壊さないことの検証である。
/// </para>
/// <para>
/// <b>ログイン試行はアプリ独自認証（<c>/admin/login/app</c>）を使う</b>: Windows 統合認証は AD
/// 環境を要し（本 CI・開発機には無い）、負荷試験の再現性を損なう。アプリ独自認証は、実在しない
/// ユーザー名でもダミーハッシュに対する PBKDF2 検証コストを払う設計（ユーザー列挙耐性。
/// security.md §2.4）のため、**管理者アカウントを作成しなくても「認証処理の CPU コスト」を
/// 現実的に再現できる**——本テストが検証したい「認証処理のコストが受信スループットを圧迫しないか」
/// を、アカウント作成という前提なしに直接検証できる。
/// </para>
/// </remarks>
public sealed class AdminAuthLoadIsolationTests : IAsyncLifetime
{
    private static readonly Regex AntiforgeryTokenPattern =
        new("name=\"__RequestVerificationToken\"\\s+value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly List<string> _issuedThumbprints = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        RemoveIssuedTestCertificates();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentAdminLoginLoad_DoesNotIncreaseUdpIngestionLoss()
    {
        using var tempDataRoot = new TestTempDirectory("bench-authload");
        var dataRoot = tempDataRoot.Path;
        Directory.CreateDirectory(dataRoot);

        var (thumbprint, _) = IssueAndInstallTestCertificate();
        BenchConfigurationFile.WriteAdminRemoteHttpsAuthLoadConfiguration(dataRoot, thumbprint, adminHttpsPort: 0);

        await using var host = await BenchHostProcess.StartAsync(dataRoot, adminPort: 0, adminHttpsPort: 0);
        var adminPort = await host.WaitForAdminPortAsync();

        var databasePath = Path.Combine(dataRoot, "yagura.db");

        // --- 区間 1: ベースライン（UDP 送信のみ） ---
        var baselineBefore = await LogStoreProbe.GetSqliteRecordCountAsync(databasePath);
        var baselineResult = await SendUdpBurstAsync(host.UdpPort, count: 500);
        var baselineAfter = await WaitForPersistedAsync(databasePath, baselineBefore, baselineResult.SentCount);
        var baselineSaved = baselineAfter - baselineBefore;

        // --- 区間 2: UDP 送信 + 管理リスナへの継続的なログイン試行を同時実行 ---
        // 認証負荷を先に始動させ、実際に最初の試行が発行されたことを確認してから並行バーストを
        // 送る。これにより「認証負荷と受信が確実に重なる」ことを保証し、かつ測定窓の締め
        // （authLoadCts.Cancel）が認証負荷のセットアップ（初回 GET）と競合してタスクが
        // TaskCanceledException で fault する、遅い CI ランナー上のレースを構造的に排除する。
        using var authLoadCts = new CancellationTokenSource();
        var authLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authLoadTask = RunContinuousLoginAttemptsAsync(adminPort, authLoadStarted, authLoadCts.Token);

        // 最初のログイン試行が発行される（または起動不能が確定する）まで待つ。起動不能のまま
        // 応答が無ければ TimeoutException で顕在化させる（負荷生成器が機能していないことを
        // 黙って通さない）。
        await authLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var concurrentBefore = baselineAfter;
        var concurrentResult = await SendUdpBurstAsync(host.UdpPort, count: 500);
        var concurrentAfter = await WaitForPersistedAsync(databasePath, concurrentBefore, concurrentResult.SentCount);
        var concurrentSaved = concurrentAfter - concurrentBefore;

        authLoadCts.Cancel();
        var loginAttempts = await authLoadTask;

        await host.StopGracefullyAsync();

        // --- 主張: 両区間とも「送信数 = 保存件数」（受信ロス 0）が成立する ---
        Assert.Equal(baselineResult.SentCount, baselineSaved);
        Assert.Equal(concurrentResult.SentCount, concurrentSaved);

        // --- 参考情報として記録するのみ（閾値判定はしない。クラスの remarks 参照） ---
        Assert.True(loginAttempts > 0, "管理リスナへのログイン試行が 1 件も実行されなかった（負荷生成器が機能していない）。");
    }

    /// <summary>
    /// 送信分が保存されるまで待って、保存後の総件数を返す（固定スリープを使わない）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>主張は変えていない</b>——期待件数に達したら抜け、達しなければ上限まで待ってから
    /// そのときの件数を返す。呼び出し側の <c>Assert.Equal(送信数, 保存数)</c> はそのままで、
    /// **本当に取りこぼしていれば従来どおり失敗する**。緩めたのは待ち方だけである。
    /// </para>
    /// <para>
    /// <b>なぜ必要か</b>: 従来は固定 2 秒の猶予だった。500 件の SQLite 永続化に要する時間は
    /// ランナーのディスク性能に依存し、混雑した個体では 2 秒に収まらない——実際に
    /// windows-2025 で「500 送信・401 保存」で落ちた（同じ run の windows-2022 は成功、
    /// 同一コミットの main も成功）。**取りこぼしではなく待ち不足**を「受信ロス」として
    /// 報告する構造になっていた。architecture.md §5.2 が記録している「CI ランナーの個体差が
    /// 支配的」という実測と同じ性質の問題である。
    /// </para>
    /// </remarks>
    private static async Task<long> WaitForPersistedAsync(string databasePath, long before, long expectedSent)
    {
        // 上限は「遅い個体でも収まる」側に取る。ここに達したら件数をそのまま返し、
        // 呼び出し側の等値検査で失敗させる（黙って成功にしない）。
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

        long after;
        do
        {
            after = await LogStoreProbe.GetSqliteRecordCountAsync(databasePath);
            if (after - before >= expectedSent)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        while (DateTimeOffset.UtcNow < deadline);

        // 到達後にもう一度読む——**超過（重複保存）も検出したい**ため、
        // 期待値に達した瞬間で打ち切らず、落ち着くまでの短い猶予を置く。
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        return await LogStoreProbe.GetSqliteRecordCountAsync(databasePath);
    }

    private static async Task<LoadGeneratorResult> SendUdpBurstAsync(int udpPort, int count)
    {
        var options = new LoadGeneratorOptions(
            Transport: LoadTransport.Udp,
            Pattern: LoadPattern.Burst,
            TargetHost: "127.0.0.1",
            TargetPort: udpPort,
            RunId: Guid.NewGuid().ToString("N"),
            BurstCount: count,
            SenderSocketCount: 2);

        return await UdpLoadGenerator.RunAsync(options);
    }

    /// <summary>
    /// 管理リスナ（loopback）へ継続的にログイン試行を送る（キャンセルされるまで）。アプリ独自認証は
    /// CSRF 対策（<c>IAntiforgery.ValidateRequestAsync</c>）を経由するため、事前に 1 回
    /// <c>/admin/login</c> を取得してトークン + Cookie を確保し、以降の POST で使い回す
    /// （既定の antiforgery トークンは Cookie の有効期間内で再利用可能——1 リクエスト 1 回限りの
    /// nonce ではない）。
    /// </summary>
    private static async Task<int> RunContinuousLoginAttemptsAsync(
        int adminPort,
        TaskCompletionSource startedSignal,
        CancellationToken cancellationToken)
    {
        var cookieContainer = new System.Net.CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookieContainer };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{adminPort}") };

        var attempts = 0;

        string token;
        try
        {
            var loginPageResponse = await httpClient.GetAsync("/admin/login", cancellationToken);
            var loginPageBody = await loginPageResponse.Content.ReadAsStringAsync(cancellationToken);
            var tokenMatch = AntiforgeryTokenPattern.Match(loginPageBody);

            if (!tokenMatch.Success)
            {
                // ページ構造の変化等で取得できない場合、認証処理そのものに到達しない POST を
                // 大量生成しても本テストの目的（PBKDF2 検証コストの再現）を達成できないため、
                // ここで諦める（0 件——呼び出し側の Assert.True(loginAttempts > 0) で顕在化する）。
                // 待機側（authLoadStarted.WaitAsync）を起こしてハングを避ける。
                startedSignal.TrySetResult();
                return 0;
            }

            token = tokenMatch.Groups[1].Value;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // セットアップ GET が停止要求と競合した（遅い CI）／一過性の接続失敗——試行 0 として
            // 返す。待機側を必ず起こす（起こさないと呼び出し側の WaitAsync が上限まで待つ）。
            startedSignal.TrySetResult();
            return attempts;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = token,
                    ["username"] = "bench-load-nonexistent-user",
                    ["password"] = "wrong-password-" + attempts,
                });

                using var response = await httpClient.PostAsync("/admin/login/app", form, cancellationToken);
                attempts++;

                // 最初の試行が HTTP レベルで完了した時点で「認証負荷が実際に始動した」ことを
                // 呼び出し側へ通知する（並行バーストはこの通知後に送られる）。
                startedSignal.TrySetResult();
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // 停止直前の一過性失敗は無視する（試行数のカウントのみが目的）。
            }
        }

        // 1 件も試行が完了しないままキャンセルされた場合の保険（待機側を確実に起こす）。
        startedSignal.TrySetResult();
        return attempts;
    }

    private (string Thumbprint, X509Certificate2 Certificate) IssueAndInstallTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=yagura-bench-admin-https-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.AddDays(365);

        using var ephemeralCertificate = request.CreateSelfSigned(notBefore, notAfter);
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
            // ベストエフォート（AdminRemoteBindingRegressionTests と同じ判断）。
        }
    }
}
