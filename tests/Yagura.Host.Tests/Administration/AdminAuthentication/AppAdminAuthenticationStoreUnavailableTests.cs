using System.Net;
using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Administration;
using Yagura.Host.Administration;
using Yagura.Host.Administration.AdminAuthentication;
using Yagura.Storage.Administration;
using Yagura.Storage.Administration.Sqlite;
using Yagura.Web.Administration;

namespace Yagura.Host.Tests.Administration.AdminAuthentication;

/// <summary>
/// 保存先が到達不能な縮退中のログイン応答の検証（ADR-0023 決定 1 受け入れ基準④。Issue #466）。
/// </summary>
/// <remarks>
/// <para>
/// 本ファイルが固定するのは、縮退応答を ADR-0011 決定 3 の列挙耐性の対象外としてよい
/// <b>成立条件</b>である（ペルソナレビュー 田中の指摘 2——「実装コメントではなく回帰テストで
/// 固定する」）。条件は 3 つ:
/// </para>
/// <list type="number">
/// <item><b>アカウント照会より前に返す</b>——実在・非実在で応答が分かれないこと。分かれると
/// 「縮退中だけ per-name オラクルが再開する」という、平常時より弱い状態を作る</item>
/// <item><b>失敗回数 n を進めない</b>——資格情報の検証に到達していないため。攻撃でバックオフを
/// 積み上げ、復旧後の正規管理者を巻き込む経路も塞ぐ</item>
/// <item><b>無コストの状態オラクルにしない</b>——リモート発は IP レート制限のカウント対象と
/// する（loopback は元々対象外）</item>
/// </list>
/// </remarks>
public sealed class AppAdminAuthenticationStoreUnavailableTests : IAsyncLifetime
{
    private static readonly AdminAuthAttemptContext LoopbackContext = new(IPAddress.Loopback, IsLoopback: true);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"yagura-store-unavailable-test-{Guid.NewGuid():N}.db");

    private SqliteAdminAccountStore _store = null!;
    private StorageAvailabilityState _availability = null!;
    private FakeTimeProvider _timeProvider = null!;
    private AppAdminAuthenticationService _service = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteAdminAccountStore(_databasePath);
        await _store.InitializeAsync();

        _availability = new StorageAvailabilityState();
        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        _service = new AppAdminAuthenticationService(
            _store, new AdminAuthFailureDefense(_timeProvider), _timeProvider, _availability);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task StoreUnavailable_ExistingAndNonexistentUsernames_ProduceIdenticalOutcomes()
    {
        await _service.SetAccountAsync("admin1", "correct-horse-battery-staple");
        _availability.MarkUnavailable("SqlException");

        var existing = await _service.TryAuthenticateAsync("admin1", "correct-horse-battery-staple", LoopbackContext);
        var existingWrongPassword = await _service.TryAuthenticateAsync("admin1", "wrong-password", LoopbackContext);
        var nonexistent = await _service.TryAuthenticateAsync("no-such-admin", "wrong-password", LoopbackContext);

        // 応答は結果種別・待機秒・拒否層のすべてで一致していなければならない（ユーザー名欄だけは
        // 監査記録のために試行値をそのまま返す仕様——利用者応答には出さない）。
        foreach (var outcome in new[] { existing, existingWrongPassword, nonexistent })
        {
            Assert.Equal(AppAuthenticationResult.StoreUnavailable, outcome.Result);
            Assert.Null(outcome.WaitSeconds);
            Assert.Equal(AdminAuthDenialLayer.None, outcome.DenialLayer);
            Assert.False(outcome.BackoffCapReached);
        }
    }

    [Fact]
    public async Task StoreUnavailable_DoesNotQueryTheAccountStore()
    {
        // 成立条件①の直接検証: 照会そのものに到達していないこと。応答の一致だけでは
        // 「照会してから応答を揃えた」実装（タイミング差が残る）を排除できない。
        var probe = new QueryCountingAdminAccountStore(_store);
        var availability = new StorageAvailabilityState();
        var service = new AppAdminAuthenticationService(
            probe, new AdminAuthFailureDefense(_timeProvider), _timeProvider, availability);

        await service.SetAccountAsync("admin1", "correct-horse-battery-staple");
        var baseline = probe.FindByUsernameCallCount;

        availability.MarkUnavailable("SqlException");
        await service.TryAuthenticateAsync("admin1", "correct-horse-battery-staple", LoopbackContext);
        await service.TryAuthenticateAsync("no-such-admin", "whatever", LoopbackContext);

        Assert.Equal(baseline, probe.FindByUsernameCallCount);
    }

    [Fact]
    public async Task StoreUnavailable_DoesNotAdvanceBackoffCounter_SoRecoveryIsImmediate()
    {
        // 成立条件②: 縮退中の試行で n が進んでいたら、復旧直後の正規管理者がバックオフ待ちに
        // 巻き込まれる（攻撃者が保存先を落とすだけで正規管理者を遅延させられることになる）。
        await _service.SetAccountAsync("admin1", "correct-horse-battery-staple");
        _availability.MarkUnavailable("SqlException");

        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold * 3; i++)
        {
            await _service.TryAuthenticateAsync("admin1", "wrong-password", LoopbackContext);
        }

        _availability.MarkAvailable();

        var outcome = await _service.TryAuthenticateAsync("admin1", "correct-horse-battery-staple", LoopbackContext);

        Assert.Equal(AppAuthenticationResult.Success, outcome.Result);
    }

    [Fact]
    public async Task StoreUnavailable_RemoteAttempts_CountAgainstIpRateLimit()
    {
        // 成立条件③: 無認証・無制限に叩ける保存先の死活オラクルにしない。IP レート制限より
        // **後**に判定しているため、リモートからの連打はやがて Denied(IpRateLimit) に転じる。
        _availability.MarkUnavailable("SqlException");
        var remote = new AdminAuthAttemptContext(IPAddress.Parse("192.0.2.10"), IsLoopback: false);

        var results = new List<AppAuthenticationResult>();
        for (var i = 0; i < 200; i++)
        {
            var outcome = await _service.TryAuthenticateAsync("admin1", "whatever", remote);
            results.Add(outcome.Result);
            if (outcome.Result == AppAuthenticationResult.Denied)
            {
                Assert.Equal(AdminAuthDenialLayer.IpRateLimit, outcome.DenialLayer);
                break;
            }
        }

        Assert.Contains(AppAuthenticationResult.Denied, results);
    }

    [Fact]
    public async Task StoreAvailableAgain_AuthenticationResumes_WithoutRestart()
    {
        // 受け入れ基準②の単体側（プロセス再起動を挟まずに回復すること）。回復までの上限時間の
        // 検証は StorageInitializationCoordinatorTests が担う。
        await _service.SetAccountAsync("admin1", "correct-horse-battery-staple");

        _availability.MarkUnavailable("SqlException");
        Assert.Equal(
            AppAuthenticationResult.StoreUnavailable,
            (await _service.TryAuthenticateAsync("admin1", "correct-horse-battery-staple", LoopbackContext)).Result);

        _availability.MarkAvailable();
        Assert.Equal(
            AppAuthenticationResult.Success,
            (await _service.TryAuthenticateAsync("admin1", "correct-horse-battery-staple", LoopbackContext)).Result);
    }

    /// <summary>
    /// <see cref="IAdminAccountStore.FindByUsernameAsync"/> の呼び出し回数を数える委譲実装。
    /// </summary>
    private sealed class QueryCountingAdminAccountStore(IAdminAccountStore inner) : IAdminAccountStore
    {
        public int FindByUsernameCallCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<AdminAccountRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
        {
            FindByUsernameCallCount++;
            return inner.FindByUsernameAsync(username, cancellationToken);
        }

        public Task<bool> HasAnyAccountAsync(CancellationToken cancellationToken = default) =>
            inner.HasAnyAccountAsync(cancellationToken);

        public Task<AdminAccountRecord?> GetSoleAccountAsync(CancellationToken cancellationToken = default) =>
            inner.GetSoleAccountAsync(cancellationToken);

        public Task UpsertAsync(
            string username, string passwordHash, DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
            inner.UpsertAsync(username, passwordHash, atUtc, cancellationToken);

        public Task RecordSuccessfulLoginAsync(
            string username, DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
            inner.RecordSuccessfulLoginAsync(username, atUtc, cancellationToken);
    }
}
