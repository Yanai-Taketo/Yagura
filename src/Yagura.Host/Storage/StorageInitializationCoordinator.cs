using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yagura.Storage;
using Yagura.Storage.Administration;
using Yagura.Web.Administration;

namespace Yagura.Host.Storage;

/// <summary>
/// 保存先（<see cref="ILogStore"/>・<see cref="IAdminAccountStore"/>）のスキーマ初期化を
/// **起動シーケンスの外**で行い、失敗しても起動を止めずに縮退させる（ADR-0023 決定 1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ起動経路から外すのか</b>: 従来は <c>Program.cs</c> が <c>app.RunAsync()</c> の前に
/// <see cref="IAdminAccountStore.InitializeAsync"/> を、<c>IngestionHostedService</c> が
/// 消費ループ開始の前に <see cref="ILogStore.InitializeAsync"/> を、いずれも try/catch なしで
/// 待っていた。保存先が SQL Server で到達不能だと <c>SqlException</c> が未処理例外となり
/// プロセスが即死する（Issue #466）——受信パイプラインは同じ障害でスプールへ正しく縮退するのに、
/// 初期化だけが縮退せず「DB 障害中は再起動できない」状態を作っていた。
/// </para>
/// <para>
/// <b>接続の天井だけでは足りない</b>（ADR-0023 決定 1・round 2 鈴木の指摘）: 初期化は接続確認では
/// なく DDL + スキーマ移行であり、<c>SqlServerLogStore</c> は移行コマンドに
/// <c>CommandTimeout = 0</c>（無制限。DB-10 の実測が根拠）を明示している。接続に上限を課しても
/// 移行が起動経路に残れば SCM の起動待ち（30 秒）は破れる。したがって**初期化そのものを
/// 起動経路の外へ出す**——起動は初期化の完了を待たない。
/// </para>
/// <para>
/// <b>不変条件</b>（ADR-0023 決定 1。委任へ落とさず決定の一部として実装する）:
/// ①<b>単一実行</b>——同時に複数の初期化を走らせない（<c>SqlServerAdminAccountStore</c> は
/// <c>LogStore</c> と違い <c>sp_getapplock</c> を持たないため、DB 側では直列化されない）。
/// ②<b>失敗の負のキャッシュ</b>——失敗直後に再試行を繰り返さない（<see cref="RetryInterval"/>）。
/// ③接続タイムアウトの明示上限は接続文字列側（<c>StorageConnectionStrings</c>）が担う。
/// </para>
/// <para>
/// <b>回復契機は周期監視のみ</b>: 「利用時（ログイン要求）」を契機にした再試行は行わない——
/// 未認証の要求で任意に保存先への接続試行を誘発でき、接続・スレッドの占有と保存先への増幅に
/// なるため（ADR-0023 決定 1。田中・クリスの指摘）。本クラスを呼ぶのは
/// <c>ActiveNotificationMonitor</c> だけである。
/// </para>
/// </remarks>
public sealed class StorageInitializationCoordinator
{
    /// <summary>
    /// 失敗後に再試行を許すまでの最小間隔（負のキャッシュ。仮値）。周期監視の間隔（1 分）より
    /// 短くしても意味がないため同オーダーに置く——「復旧したら自動で戻る」体験（スプールの
    /// drain と同じ）を損なわない範囲で、障害中の接続試行を最小に保つ。
    /// </summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// **起動シーケンス中**に保存先への接続へ費やしてよい合計時間の上限（ADR-0023 決定 1。
    /// SCM の起動待ち 30 秒に対する余裕）。スキーマ初期化は本クラスが起動経路の外で行うため、
    /// この天井が掛かるのは残る起動時照会——自己ロックアウト点検の <c>HasAnyAccountAsync</c>——
    /// のみである。**稼働中の書き込みタイムアウト（M-13）には及ばない**——AG フェイルオーバーや
    /// 名前付きインスタンス解決で正常系が数十秒かかる経路を、起動用の天井で切らないため。
    /// </summary>
    internal static readonly TimeSpan StartupProbeCeiling = TimeSpan.FromSeconds(10);

    private readonly ILogStore _logStore;
    private readonly IAdminAccountStore _accountStore;
    private readonly StorageAvailabilityState _state;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>単一実行のゲート（不変条件①）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset? _lastAttemptAt;
    private bool _logStoreInitialized;
    private bool _accountStoreInitialized;
    private string? _lastAccountStoreFailureKind;

    public StorageInitializationCoordinator(
        ILogStore logStore,
        IAdminAccountStore accountStore,
        StorageAvailabilityState state,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(accountStore);
        ArgumentNullException.ThrowIfNull(state);

        _logStore = logStore;
        _accountStore = accountStore;
        _state = state;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>両ストアの初期化が完了しているか（回復判定・通知の抑制に使う）。</summary>
    public bool IsFullyInitialized => _logStoreInitialized && _accountStoreInitialized;

    /// <summary>ログストアのスキーマ初期化が完了しているか。</summary>
    /// <remarks>
    /// 通知の文面を正確にするために公開する——アカウントストアだけが落ちているのか、ログストアも
    /// 落ちているのかで**運用者が取るべき行動が違う**（前者は認証の縮退、後者はスプールの残量監視）。
    /// 「初期化に失敗した」だけを一括で伝えると、どちらの手当てが要るのか判断できない。
    /// </remarks>
    public bool IsLogStoreInitialized => _logStoreInitialized;

    /// <summary>管理者アカウントストアのスキーマ初期化が完了しているか（= アプリ独自認証の可用性）。</summary>
    public bool IsAdminAccountStoreInitialized => _accountStoreInitialized;

    /// <summary>
    /// 未完了の初期化を試みる。**失敗しても例外を投げない**——戻り値と
    /// <see cref="StorageAvailabilityState"/> で結果を伝える（呼び出し側＝周期監視のループを
    /// 初期化失敗で止めないため）。
    /// </summary>
    /// <param name="force">
    /// 負のキャッシュ（<see cref="RetryInterval"/>）を無視して試行する。監視ループの初回
    /// （起動直後の一回）でのみ true にする——健全な環境で初期化が最大 1 周期遅れないようにする。
    /// </param>
    /// <returns>本呼び出しの後に両ストアの初期化が完了していれば <see langword="true"/>。</returns>
    public async Task<bool> TryInitializeAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (IsFullyInitialized)
        {
            return true;
        }

        // 不変条件②: 失敗直後の再試行を抑える（周期評価のたびに接続試行を積まない）。
        var now = _timeProvider.GetUtcNow();
        if (!force && _lastAttemptAt is { } lastAttempt && now - lastAttempt < RetryInterval)
        {
            return false;
        }

        // 不変条件①: 単一実行。取得できなければ「別の試行が進行中」であり、待たずに諦める
        // （監視ループを塞がない——次の周期で結果が反映される）。
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            _lastAttemptAt = _timeProvider.GetUtcNow();

            // ログストア: 失敗しても受信は継続する（書き込みはスプールへ退避される——
            // architecture.md §1.2 の縮退運転。ここで初期化できなければスキーマが無いだけで、
            // 受信そのものは既に始まっている）。
            if (!_logStoreInitialized)
            {
                _logStoreInitialized = await TryInitializeOneAsync(
                    "ログストア", ct => _logStore.InitializeAsync(ct), onFailure: null, cancellationToken).ConfigureAwait(false);
            }

            // 管理者アカウントストア: 失敗するとアプリ独自認証が使えない（縮退。ADR-0023 決定 1）。
            if (!_accountStoreInitialized)
            {
                _lastAccountStoreFailureKind = null;
                _accountStoreInitialized = await TryInitializeOneAsync(
                    "管理者アカウントストア",
                    ct => _accountStore.InitializeAsync(ct),
                    kind => _lastAccountStoreFailureKind = kind,
                    cancellationToken).ConfigureAwait(false);
            }

            // 縮退状態は**管理者アカウントストアの結果だけ**で決める（StorageAvailabilityState が
            // 表すのはアプリ独自認証の可用性であり、ログストアの可否ではない）。ログストアの失敗で
            // ここを落とすと、アカウントストアは健全なのにログインが一時的に拒否される
            // ——同じ DB でも失敗するとは限らない（権限や既存スキーマの差で片方だけ落ちうる）。
            if (_accountStoreInitialized)
            {
                _state.MarkAvailable();
            }
            else
            {
                _state.MarkUnavailable(_lastAccountStoreFailureKind);
            }

            return IsFullyInitialized;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> TryInitializeOneAsync(
        string label,
        Func<CancellationToken, Task> initialize,
        Action<string>? onFailure,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await initialize(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "{Label}のスキーマ初期化が完了しました（所要 {ElapsedMs} ミリ秒）。", label, stopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 例外の種別は問わない（保存先の到達不能・権限不足・スキーマ移行の失敗のいずれでも
            // 「起動を止めない」が本決定の要点。失敗の可視化は能動通知が担う）。
            onFailure?.Invoke(ex.GetType().Name);
            _logger.LogWarning(
                ex,
                "{Label}のスキーマ初期化に失敗しました（所要 {ElapsedMs} ミリ秒）。" +
                "起動は継続します——保存先が復旧すると自動的に再試行します（ADR-0023 決定 1）。",
                label,
                stopwatch.ElapsedMilliseconds);
            return false;
        }
    }
}
