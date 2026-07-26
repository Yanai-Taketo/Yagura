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
/// <para>
/// <b>失敗試行対策は本契約の管轄外（ADR-0011 決定 8）</b>: ADR-0010 決定 3 のハードロックアウトが
/// 使っていた <c>FailedAttemptCount</c>/<c>LockoutUntilUtc</c> の 2 列は ADR-0011（バックオフ +
/// レート制限への supersession）で削除マイグレーションを適用した。失敗試行の判定はインメモリの
/// バックオフ状態（<c>Yagura.Host.Administration.AdminAuthentication.AdminAuthFailureDefense</c>）に
/// 一本化しており、本契約は「アカウントが存在するか」「資格情報は何か」「最終ログイン時刻」の
/// 3 つのみを扱う——「書くが判定には使わない」中間状態は選ばない（二重の真実源を作らない。
/// ADR-0011 決定 8）。
/// </para>
/// <para>
/// <b>作成・変更時刻を保持する（ADR-0021 決定 1 の切替時点検）</b>: フォワーダ MSI アップロード
/// 機能の有効化動線は「既存アプリアカウントの有無・最終変更時刻」を管理者へ提示し、そのアカウントが
/// 自分の管理下にあることの確認を求める（無認証 loopback で事前に仕込まれた資格情報による迂回の
/// 検出可能性を残すため）。この提示に必要な <see cref="AdminAccountRecord.CreatedAtUtc"/>/
/// <see cref="AdminAccountRecord.UpdatedAtUtc"/> をスキーマ v3 で追加した。**v3 より前に作成された
/// 既存行の両列は <see langword="null"/>（= 不明）になる**——確定的な値を後から捏造しないため
/// （UI 側は「不明」と表示する。<see cref="AdminAccountRecord.LastLoginAtUtc"/> の未ログイン =
/// null と同じ流儀）。
/// </para>
/// </remarks>
public interface IAdminAccountStore
{
    /// <summary>スキーマを初期化する（冪等。<see cref="ILogStore.InitializeAsync"/> と同じ規約。
    /// ADR-0011 決定 8 の削除マイグレーション〔v1 → v2〕と ADR-0021 の時刻列追加〔v2 → v3〕を
    /// 含む）。</summary>
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
    /// パスワードハッシュを更新する（アップサート）。
    /// </summary>
    /// <param name="atUtc">
    /// 操作時刻（UTC）。新規作成時は <see cref="AdminAccountRecord.CreatedAtUtc"/> と
    /// <see cref="AdminAccountRecord.UpdatedAtUtc"/> の両方に、更新時は
    /// <see cref="AdminAccountRecord.UpdatedAtUtc"/> のみに記録する（ADR-0021 決定 1 の切替時点検が
    /// 使う）。時刻源は呼び出し側が持つ（<see cref="RecordSuccessfulLoginAsync"/> と同じ流儀——
    /// ストアに <c>TimeProvider</c> を持たせず、テストが時刻を決定的に固定できるようにする）。
    /// </param>
    Task UpsertAsync(string username, string passwordHash, DateTimeOffset atUtc, CancellationToken cancellationToken = default);

    /// <summary>ログイン成功を記録する（最終ログイン時刻の更新）。</summary>
    Task RecordSuccessfulLoginAsync(string username, DateTimeOffset atUtc, CancellationToken cancellationToken = default);
}

/// <summary>管理者アカウント 1 件分の永続化済み状態。</summary>
/// <param name="Username">ユーザー名（元の大小文字のまま）。</param>
/// <param name="PasswordHash"><c>PasswordHasher&lt;TUser&gt;</c> 形式のハッシュ文字列。</param>
/// <param name="LastLoginAtUtc">直近の成功ログイン時刻（UTC）。未ログインなら <see langword="null"/>。</param>
/// <param name="CreatedAtUtc">
/// アカウントの作成時刻（UTC。ADR-0021 決定 1 の切替時点検）。スキーマ v3 より前に作成された
/// アカウントでは <see langword="null"/>（= 不明。後から確定的な値を捏造しない）。
/// </param>
/// <param name="UpdatedAtUtc">
/// 直近の変更時刻（UTC。パスワード変更・ユーザー名の大小文字変更を含むアップサート）。
/// スキーマ v3 より前に更新されたきりのアカウントでは <see langword="null"/>（= 不明）。
/// </param>
public sealed record AdminAccountRecord(
    string Username,
    string PasswordHash,
    DateTimeOffset? LastLoginAtUtc,
    DateTimeOffset? CreatedAtUtc = null,
    DateTimeOffset? UpdatedAtUtc = null);
