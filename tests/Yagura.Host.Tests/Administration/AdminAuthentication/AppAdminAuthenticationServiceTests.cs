using System.Net;
using Microsoft.Extensions.Time.Testing;
using Yagura.Abstractions.Administration;
using Yagura.Host.Administration;
using Yagura.Host.Administration.AdminAuthentication;
using Yagura.Storage.Administration.Sqlite;

namespace Yagura.Host.Tests.Administration.AdminAuthentication;

/// <summary>
/// <see cref="AppAdminAuthenticationService"/> の単体テスト（ADR-0010 決定 3。ADR-0011 決定 2〜7
/// の三層防御・パスワード強度要件を検証する）。
/// </summary>
/// <remarks>
/// 検証観点: (1) 正しい資格情報での成功、(2) 誤パスワードでの失敗、(3) 存在しないユーザー名でも
/// 同一の <see cref="AppAuthenticationResult.InvalidCredentials"/> を返し個別状態を持たない
/// （ユーザー列挙耐性・メモリ枯渇 DoS 回避）、(4) バックオフ猶予閾値到達で
/// <see cref="AppAuthenticationResult.Denied"/>（<see cref="AdminAuthDenialLayer.Backoff"/>）、
/// (5) 成功で n がリセットされる、(6) IP レート制限・グローバルトークンバケットが評価順序どおり
/// パスワード検証の手前で作用する、(7) loopback は IP レート制限・グローバルバケットの対象外、
/// (8) パスワード変更後は旧パスワードで失敗する、(9) パスワード強度要件（最小長・ブロックリスト）。
/// </remarks>
public sealed class AppAdminAuthenticationServiceTests : IAsyncLifetime
{
    private static readonly AdminAuthAttemptContext LoopbackContext = new(IPAddress.Loopback, IsLoopback: true);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"yagura-app-auth-test-{Guid.NewGuid():N}.db");
    private SqliteAdminAccountStore _store = null!;

    /// <summary>
    /// 主に使う既定インスタンス（<see cref="FakeTimeProvider"/> 駆動）。バックオフの実待機
    /// （<c>Task.Delay(delay, timeProvider, ct)</c>）を経由する試行には使わない——
    /// <see cref="FakeTimeProvider"/> のタイマーは明示的に <c>Advance</c> しない限り発火しないため、
    /// 待機を伴う試行は <see cref="_realTimeService"/>（既定 <see cref="TimeProvider.System"/>）を使う。
    /// </summary>
    private AppAdminAuthenticationService _service = null!;
    private FakeTimeProvider _timeProvider = null!;

    /// <summary>
    /// バックオフの実待機を伴うテスト専用（仮値 base=1 秒・k=3 のため、n=k+1 到達直後の待機は
    /// 1 秒程度——実時間で許容できる範囲に収める）。
    /// </summary>
    private AppAdminAuthenticationService _realTimeService = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteAdminAccountStore(_databasePath);
        await _store.InitializeAsync();

        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-10T00:00:00Z"));
        _service = new AppAdminAuthenticationService(_store, new AdminAuthFailureDefense(_timeProvider), _timeProvider);
        _realTimeService = new AppAdminAuthenticationService(_store, new AdminAuthFailureDefense());
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
    public async Task TryAuthenticateAsync_CorrectCredentials_Succeeds()
    {
        await _service.SetAccountAsync("admin1", "correct-horse-battery-staple");

        var outcome = await _service.TryAuthenticateAsync("admin1", "correct-horse-battery-staple", LoopbackContext);

        Assert.Equal(AppAuthenticationResult.Success, outcome.Result);
        Assert.Equal("admin1", outcome.Username);
    }

    [Fact]
    public async Task TryAuthenticateAsync_WrongPassword_ReturnsInvalidCredentials_BelowBackoffThreshold()
    {
        await _service.SetAccountAsync("admin1", "correct-password");

        var outcome = await _service.TryAuthenticateAsync("admin1", "wrong-password", LoopbackContext);

        Assert.Equal(AppAuthenticationResult.InvalidCredentials, outcome.Result);
        Assert.Null(outcome.WaitSeconds);
    }

    [Fact]
    public async Task TryAuthenticateAsync_UnknownUsername_ReturnsSameResultAsWrongPassword_EnumerationResistance()
    {
        // ユーザー列挙耐性(ADR-0011 決定 3): 存在しないユーザー名でも実在アカウントの誤パスワードと
        // 同一の結果種別を返す(応答内容で区別しない)。
        await _service.SetAccountAsync("admin1", "correct-password");

        var unknownUserOutcome = await _service.TryAuthenticateAsync("no-such-user", "whatever", LoopbackContext);
        var wrongPasswordOutcome = await _service.TryAuthenticateAsync("admin1", "wrong-password", LoopbackContext);

        Assert.Equal(AppAuthenticationResult.InvalidCredentials, unknownUserOutcome.Result);
        Assert.Equal(AppAuthenticationResult.InvalidCredentials, wrongPasswordOutcome.Result);
    }

    [Fact]
    public async Task TryAuthenticateAsync_UnknownUsername_RepeatedAttempts_NeverGetsDenied_NoIndividualState()
    {
        // ADR-0011 決定 3 の核心: 非実在ユーザー名には個別のバックオフ状態を持たせない
        // （メモリ枯渇 DoS の回避）。loopback（IP レート制限・グローバルバケットの対象外）で
        // 何度叩いても、サービス層では常に同一の InvalidCredentials（待機なし）のままであること。
        //
        // 注: 実在アカウントは閾値超過で Denied(Backoff) を返し、サービス層では非実在（InvalidCredentials）
        // と結果種別が異なる——ただしこの差は監査記録（層の別。決定 9）のためのものであり、
        // **クライアントが観測する HTTP 応答は AdminAuthEndpoints が両者を error=1 に統一する**
        // （列挙耐性の実証は AdminAuthLoginEndpointTests
        // .AppLogin_BackoffWaiting_And_NonexistentUser_ProduceByteIdenticalClientResponse_...）。
        for (var i = 0; i < 50; i++)
        {
            var outcome = await _service.TryAuthenticateAsync("no-such-user", "whatever", LoopbackContext);
            Assert.Equal(AppAuthenticationResult.InvalidCredentials, outcome.Result);
            Assert.Null(outcome.WaitSeconds);
        }
    }

    [Fact]
    public async Task TryAuthenticateAsync_SuccessResetsBackoffState()
    {
        await _service.SetAccountAsync("admin1", "correct-password");

        await _service.TryAuthenticateAsync("admin1", "wrong-password", LoopbackContext);
        var success = await _service.TryAuthenticateAsync("admin1", "correct-password", LoopbackContext);

        Assert.Equal(AppAuthenticationResult.Success, success.Result);

        // 成功後は再び閾値未満の失敗として扱われる（n がリセットされている）。
        var afterSuccess = await _service.TryAuthenticateAsync("admin1", "wrong-password", LoopbackContext);
        Assert.Equal(AppAuthenticationResult.InvalidCredentials, afterSuccess.Result);
    }

    [Fact]
    public async Task SetAccountAsync_ChangesPassword_OldPasswordNoLongerWorks()
    {
        await _service.SetAccountAsync("admin1", "old-password");
        await _service.SetAccountAsync("admin1", "new-password");

        var withOldPassword = await _service.TryAuthenticateAsync("admin1", "old-password", LoopbackContext);
        var withNewPassword = await _service.TryAuthenticateAsync("admin1", "new-password", LoopbackContext);

        Assert.Equal(AppAuthenticationResult.InvalidCredentials, withOldPassword.Result);
        Assert.Equal(AppAuthenticationResult.Success, withNewPassword.Result);
    }

    [Fact]
    public async Task SetAccountAsync_PasswordTooShort_ThrowsPasswordPolicyViolation()
    {
        var ex = await Assert.ThrowsAsync<AdminPasswordPolicyViolationException>(
            () => _service.SetAccountAsync("admin1", "short7chr"));

        Assert.Contains("12 文字以上", ex.Message);
    }

    [Fact]
    public async Task SetAccountAsync_BlocklistedPassword_ThrowsPasswordPolicyViolation()
    {
        // 「000000000000」（12 桁の 0）は同梱ブロックリストに含まれる既知の頻出パスワード。
        var ex = await Assert.ThrowsAsync<AdminPasswordPolicyViolationException>(
            () => _service.SetAccountAsync("admin1", "000000000000"));

        Assert.Contains("ブロックリスト", ex.Message);
    }

    [Fact]
    public async Task TryAuthenticateAsync_LoopbackContext_NeverDeniedByIpRateLimitOrGlobalBucket_RealTime()
    {
        // ADR-0011 決定 4: loopback は IP レート制限・グローバルトークンバケットの対象外。
        // バックオフ猶予閾値 k を超えて実際にバックオフが作動する状態まで試行を重ねても、
        // これらの層による Denied は発生しない（バックオフ層が別途作用し得るが、その場合の
        // DenialLayer は Backoff のみ）。実時間の待機（k+2 回で最大 1+2=3 秒程度）を伴うため
        // _realTimeService を使う。
        await _realTimeService.SetAccountAsync("admin1", "correct-password");

        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold + 2; i++)
        {
            var outcome = await _realTimeService.TryAuthenticateAsync("admin1", "wrong-password", LoopbackContext);
            Assert.NotEqual(AdminAuthDenialLayer.IpRateLimit, outcome.DenialLayer);
            Assert.NotEqual(AdminAuthDenialLayer.GlobalBucket, outcome.DenialLayer);
        }
    }

    [Fact]
    public async Task TryAuthenticateAsync_RemoteIpExceedsRateLimit_DeniedBeforePasswordVerification()
    {
        // 評価順序 ①（決定 2）: IP レート制限で拒否された試行はパスワード検証まで到達しない——
        // 正しいパスワードを渡しても Denied のままであることで、検証前拒否であることを確認する。
        await _service.SetAccountAsync("admin1", "correct-password");
        var remote = new AdminAuthAttemptContext(IPAddress.Parse("203.0.113.10"), IsLoopback: false);

        for (var i = 0; i < AdminAuthenticationDefaults.IpRateLimitMaxAttempts; i++)
        {
            var outcome = await _service.TryAuthenticateAsync("admin1", "correct-password", remote);
            Assert.Equal(AppAuthenticationResult.Success, outcome.Result);

            // 成功試行のたびに n をリセットしても、①はパスワード検証の手前で判定するため
            // IP レート制限の窓カウントには無関係に進む。
        }

        var denied = await _service.TryAuthenticateAsync("admin1", "correct-password", remote);

        Assert.Equal(AppAuthenticationResult.Denied, denied.Result);
        Assert.Equal(AdminAuthDenialLayer.IpRateLimit, denied.DenialLayer);
        Assert.True(denied.WaitSeconds > 0);
    }

    [Fact]
    public async Task TryAuthenticateAsync_GlobalBucketExhaustedAcrossManyDistinctIps_DeniesFurtherAttempts()
    {
        // 評価順序 ②（決定 2）・単一アカウントモデル固有の広域探索対策（決定 10 †）: 送信元 IP を
        // 分散しても、プロセス全体のトークン総量（バースト 20）で頭打ちになることを確認する。
        // 各 IP は IP レート制限の窓上限（10 回）に達しない範囲で 1 回ずつ使う。
        await _service.SetAccountAsync("admin1", "correct-password");

        for (var i = 0; i < AdminAuthenticationDefaults.GlobalBucketBurst; i++)
        {
            var context = new AdminAuthAttemptContext(IPAddress.Parse($"198.51.100.{i + 1}"), IsLoopback: false);
            var outcome = await _service.TryAuthenticateAsync("admin1", "correct-password", context);
            Assert.Equal(AppAuthenticationResult.Success, outcome.Result);
        }

        var extraContext = new AdminAuthAttemptContext(IPAddress.Parse("198.51.100.201"), IsLoopback: false);
        var denied = await _service.TryAuthenticateAsync("admin1", "correct-password", extraContext);

        Assert.Equal(AppAuthenticationResult.Denied, denied.Result);
        Assert.Equal(AdminAuthDenialLayer.GlobalBucket, denied.DenialLayer);
        Assert.True(denied.WaitSeconds > 0);
    }

    [Fact]
    public async Task TryAuthenticateAsync_BackoffThresholdReached_DelaysAndReturnsDenied_RealTime()
    {
        // 決定 3: 連続失敗回数 n が猶予閾値 k を超えると、パスワード検証の前に遅延を適用する。
        // 実時間の待機を伴うため FakeTimeProvider ではなく既定 TimeProvider を使う専用インスタンス
        // （_realTimeService）で検証する。base=1 秒・k=3 のため、閾値到達直後の遅延は約 1 秒。
        await _realTimeService.SetAccountAsync("admin1", "correct-password");

        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold; i++)
        {
            var outcome = await _realTimeService.TryAuthenticateAsync("admin1", "wrong-password", LoopbackContext);
            Assert.Equal(AppAuthenticationResult.InvalidCredentials, outcome.Result);
        }

        var denied = await _realTimeService.TryAuthenticateAsync("admin1", "wrong-password", LoopbackContext);

        Assert.Equal(AppAuthenticationResult.Denied, denied.Result);
        Assert.Equal(AdminAuthDenialLayer.Backoff, denied.DenialLayer);
        Assert.True(denied.WaitSeconds is > 0);
    }

    [Fact]
    public async Task TryAuthenticateAsync_BackoffActive_CorrectPasswordStillSucceedsAfterDelay_RealTime()
    {
        // 「正しいパスワードなら待てば必ず通る」（決定 3 の核心）: バックオフ猶予閾値超過後でも、
        // 正しいパスワードは（遅延を負ったうえで）成功する——完全拒否は行わない。
        await _realTimeService.SetAccountAsync("admin1", "correct-password");

        for (var i = 0; i < AdminAuthenticationDefaults.BackoffThreshold; i++)
        {
            await _realTimeService.TryAuthenticateAsync("admin1", "wrong-password", LoopbackContext);
        }

        var outcome = await _realTimeService.TryAuthenticateAsync("admin1", "correct-password", LoopbackContext);

        Assert.Equal(AppAuthenticationResult.Success, outcome.Result);
    }
}
