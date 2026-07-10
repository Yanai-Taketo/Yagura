namespace Yagura.Storage.Administration;

/// <summary>
/// 管理 UI アプリ独自 ID/パスワード認証（ADR-0010 決定 3）の管理者アカウント永続化契約。
/// </summary>
/// <remarks>
/// <para>
/// <b>単一アカウントモデル（Phase 1）</b>: ADR-0010 決定 3 は「最初の管理者アカウント」の
/// loopback 中セットアップを Phase 1 のスコープとする。複数アカウントの追加・削除・一覧の
/// 管理画面は Phase 1 のスコープ外——本契約は「アカウントが存在するか」「存在するならその 1 件」
/// を扱えれば足り、<see cref="ILogStore"/> のような汎用クエリ API は持たない（規模に見合った
/// 最小実装。ADR-0010 検討した選択肢「アプリ独自認証の実装方式」参照）。
/// </para>
/// <para>
/// <b>既存のデータ provider 抽象に載る単一テーブル（ADR-0010 決定 3）</b>: <see cref="ILogStore"/>
/// はログレコード専用の形をした契約であり、管理者アカウント（1 行程度の小さな行政データ）を
/// そこへ載せると契約の性質が歪む。そのため <see cref="ILogStore"/> とは独立の小さな並行契約とし、
/// 同じ DB provider 選択（SQLite/SQL Server）・同じ接続情報にそのまま相乗りする（Yagura.Host の
/// 結線パターンは <c>ILogStore</c> と同じ——Program.cs の provider 切替 switch を参照）。
/// </para>
/// <para>
/// <b>ユーザー名の大小文字（casing）</b>: <see cref="FindByUsernameAsync"/> は大文字小文字を
/// 区別しない照合を行う（Windows のユーザー名慣行・入力ミス耐性のため）。表示用の元の大小文字は
/// <see cref="AdminAccountRecord.Username"/> にそのまま保持される。
/// </para>
/// </remarks>
public interface IAdminAccountStore
{
    /// <summary>スキーマを初期化する（冪等。<see cref="ILogStore.InitializeAsync"/> と同じ規約）。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>アプリ独自認証の管理者アカウントが 1 件でも存在するかどうか。</summary>
    Task<bool> HasAnyAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 唯一の管理者アカウント（Phase 1 の単一アカウントモデル）を返す。複数存在する状況は
    /// 想定しない——存在すれば最初の 1 件を返す（管理画面の状態表示用）。
    /// </summary>
    Task<AdminAccountRecord?> GetSoleAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>ユーザー名（大小文字を区別しない）でアカウントを検索する。</summary>
    Task<AdminAccountRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// アカウントを作成、または既存アカウント（Phase 1 は単一アカウントのため常に同一ユーザー名）の
    /// パスワードハッシュを更新する（アップサート）。失敗試行カウンタ・ロックアウトはリセットされる。
    /// </summary>
    Task UpsertAsync(string username, string passwordHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// ログイン成功を記録する（失敗試行カウンタ・ロックアウトのリセット、最終ログイン時刻の更新）。
    /// </summary>
    Task RecordSuccessfulLoginAsync(string username, DateTimeOffset atUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// ログイン失敗を記録し、失敗試行カウンタを増分する。増分後のカウンタが
    /// <paramref name="lockoutThreshold"/> 以上になった場合、<paramref name="lockoutDuration"/> の
    /// ロックアウトを設定する。戻り値は増分後の状態（呼び出し側が監査記録・応答の分岐に使う）。
    /// </summary>
    Task<AdminAccountLoginFailureResult> RecordFailedLoginAsync(
        string username,
        DateTimeOffset atUtc,
        int lockoutThreshold,
        TimeSpan lockoutDuration,
        CancellationToken cancellationToken = default);
}

/// <summary>管理者アカウント 1 件分の永続化済み状態。</summary>
/// <param name="Username">ユーザー名（元の大小文字のまま）。</param>
/// <param name="PasswordHash"><c>PasswordHasher&lt;TUser&gt;</c> 形式のハッシュ文字列。</param>
/// <param name="FailedAttemptCount">直近の連続失敗試行回数（成功ログインでリセット）。</param>
/// <param name="LockoutUntilUtc">ロックアウト解除時刻（UTC）。ロックアウト中でなければ <see langword="null"/>。</param>
/// <param name="LastLoginAtUtc">直近の成功ログイン時刻（UTC）。未ログインなら <see langword="null"/>。</param>
public sealed record AdminAccountRecord(
    string Username,
    string PasswordHash,
    int FailedAttemptCount,
    DateTimeOffset? LockoutUntilUtc,
    DateTimeOffset? LastLoginAtUtc);

/// <summary><see cref="IAdminAccountStore.RecordFailedLoginAsync"/> の結果。</summary>
/// <param name="FailedAttemptCount">増分後の連続失敗試行回数。</param>
/// <param name="LockedOutNow">
/// 今回の増分でロックアウト閾値に達し、新規にロックアウトが設定されたかどうか
/// （既にロックアウト中だった場合は <see langword="false"/>——重複監査記録を避けるための区別）。
/// </param>
/// <param name="LockoutUntilUtc">ロックアウト解除時刻（UTC）。ロックアウト中でなければ <see langword="null"/>。</param>
public sealed record AdminAccountLoginFailureResult(
    int FailedAttemptCount,
    bool LockedOutNow,
    DateTimeOffset? LockoutUntilUtc);
