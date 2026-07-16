namespace Yagura.Abstractions.Administration;

/// <summary>
/// 本番昇格後の蓄積ログ移行（SQLite → SQL Server。database.md §6.2。DB-5。Issue #266）。
/// 昇格ウィザードの切替実行 + サービス再起動の**後**に、旧 SQLite ファイルに残った蓄積ログを
/// 現行 provider（SQL Server）へ移送する管理操作。
/// </summary>
/// <remarks>
/// 書き込み系契約（<see cref="IYaguraWriteService"/>——security.md §1 L-5 の参照分離対象）。
/// 実行は database.md §6.2 の固定要件 6 点（受信継続・完全性検証内蔵・中断再開安全・
/// ReceivedAt 不変・移行由来の識別・残置しない〔処分は DB-7 側〕）に従う。
/// </remarks>
public interface ILogMigrationService
{
    /// <summary>現在の移行の可否・進行状態を返す（画面表示用。副作用なし）。</summary>
    Task<LogMigrationStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 移行を実行する（中断済みならチェックポイントから再開する）。完了まで戻らない——
    /// 呼び出し側（画面）は <paramref name="progress"/> で進捗を受け取る。
    /// </summary>
    Task<LogMigrationResult> RunAsync(
        string? operatorAddress,
        string? authenticationScheme,
        string? authenticatedPrincipal,
        IProgress<LogMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>移行の可否・進行状態。</summary>
/// <param name="Availability">可否の分類。</param>
/// <param name="SourceRecordCount">旧 SQLite に残っている移行対象件数（可用時のみ）。</param>
/// <param name="MigratedCount">チェックポイント上の移行済み件数（中断からの再開時に非 0）。</param>
public sealed record LogMigrationStatus(
    LogMigrationAvailability Availability,
    long? SourceRecordCount = null,
    long MigratedCount = 0);

/// <summary>移行の可否の分類。</summary>
public enum LogMigrationAvailability
{
    /// <summary>現行 provider が SQL Server ではない（昇格前）。</summary>
    NotPromoted,

    /// <summary>旧 SQLite ファイルが存在しない（移行対象なし・処分済み）。</summary>
    NoSourceDatabase,

    /// <summary>実行可能（未実行または中断からの再開）。</summary>
    Ready,

    /// <summary>完了済み（チェックポイントに完了記録あり。旧ファイルの処分は DB-7 の手順へ）。</summary>
    Completed,
}

/// <summary>移行の進捗通知。</summary>
/// <param name="MigratedCount">移行済み件数（累計。再開分を含む）。</param>
/// <param name="SourceRecordCount">移行対象の総件数。</param>
public sealed record LogMigrationProgress(long MigratedCount, long SourceRecordCount);

/// <summary>移行 1 回の結果。</summary>
/// <param name="Succeeded">完了し、完全性検証（件数突合）にも合格したか。</param>
/// <param name="MigratedCount">この実行を含む累計移行件数。</param>
/// <param name="SourceRecordCount">移行元の総件数。</param>
/// <param name="TargetCountInRange">検証時に数えた移行先の件数（移行範囲内）。</param>
/// <param name="Message">結果の説明（検証の合否根拠・失敗理由）。</param>
public sealed record LogMigrationResult(
    bool Succeeded,
    long MigratedCount,
    long SourceRecordCount,
    long TargetCountInRange,
    string Message);
