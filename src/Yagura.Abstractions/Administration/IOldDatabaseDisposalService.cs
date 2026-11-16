namespace Yagura.Abstractions.Administration;

/// <summary>
/// 本番昇格の最終段——旧・組み込み DB ファイルの処分（database.md §6.3）。画面上の呼び名は
/// 「後始末」、退避は「書き出して保管」（ui.md §7.2）。
/// </summary>
/// <remarks>
/// <para>
/// 処分は再起動と移行の後に行う管理操作であり、実行はサービス側の処理として完走する——
/// <see cref="StartAsync"/> は前提条件と検証を通過したら直ちに戻り、結果は
/// <see cref="GetStatusAsync"/> の <see cref="OldDatabaseDisposalStatus.LastResult"/> で受け取る
/// （画面を離れても・circuit が失われても処分を途中で放棄しない）。
/// </para>
/// <para>
/// <see cref="GetStatusAsync"/> は旧ファイルを開かずに判定する（/admin の常設カード・能動通知と
/// 同じ材料）。旧ファイルを開く照合は <see cref="InspectAsync"/> と処分の実行に限り、
/// 移行と同じ排他の下で行う。
/// </para>
/// </remarks>
public interface IOldDatabaseDisposalService : IYaguraWriteService
{
    /// <summary>状態表（database.md §6.3）の現在の状態を返す。旧ファイルは開かない。</summary>
    Task<OldDatabaseDisposalStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 確認ダイアログの材料（対象の安全検査・移行の完了判定・件数と期間）を求める。旧ファイルを
    /// 移行と同じ排他の下で読む。何も変更しない。
    /// </summary>
    Task<OldDatabaseDisposalInspection> InspectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 書き出して保管の保管先（親フォルダ）を、何も変更せずに検査する（入力の直後の案内用）。
    /// フォルダを作れるかどうかは実行時に作業用サブフォルダを作って確かめるため、ここでは判定しない。
    /// </summary>
    Task<OldDatabaseArchiveDestinationCheck> CheckArchiveDestinationAsync(
        string parentDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 処分を開始する。前提条件・対象の安全検査・保管先の条件をすべて満たしたときだけ
    /// <see cref="OldDatabaseDisposalOutcome.Started"/> を返して実行を始める（以後の進捗・結果は
    /// <see cref="GetStatusAsync"/>）。満たさなければ何も変更せずに拒否する。
    /// </summary>
    Task<OldDatabaseDisposalResult> StartAsync(
        OldDatabaseDisposalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 実行中の処分を取り消す。削除の段階に入る前だけ取り消せる（取り消しは退避の失敗と同じ扱い）。
    /// 取り消しを受け付けたら <see langword="true"/>。
    /// </summary>
    Task<bool> CancelAsync(CancellationToken cancellationToken = default);
}

/// <summary>処分の方式。既定の選択は置かない（利用者に能動的に選ばせる）。</summary>
public enum OldDatabaseDisposalMethod
{
    /// <summary>書き出して保管（設計書の内部用語では退避）。検証に合格してから元を削除する。</summary>
    Archive,

    /// <summary>元のファイルを削除する。</summary>
    Delete,
}

/// <summary>状態表（database.md §6.3）の状態。</summary>
public enum OldDatabaseDisposalState
{
    /// <summary>対象外（実効 provider が SQL Server でない、または処分の対象がいずれも存在しない。完了を含む）。</summary>
    NotApplicable,

    /// <summary>処分待ち。</summary>
    Pending,

    /// <summary>移行の実行中（移行が旧ファイルの排他を保持している）。</summary>
    MigrationRunning,

    /// <summary>処分の実行中（削除の段階の前。取り消せる）。</summary>
    DisposalRunning,

    /// <summary>処分の実行中（削除の段階。取り消せない）。</summary>
    DisposalDeleting,

    /// <summary>処分待ちの条件 + このプロセスで直前の処分が失敗・部分失敗・中断で終わった。</summary>
    LastAttemptFailed,
}

/// <summary>処分の対象ファイル 1 件。</summary>
/// <param name="Path">フルパス。</param>
/// <param name="SizeBytes">サイズ（読めなかった場合は <see langword="null"/>）。</param>
public sealed record OldDatabaseFile(string Path, long? SizeBytes);

/// <summary>状態表の現在の状態（旧ファイルを開かずに判定した事実）。</summary>
/// <param name="State">状態。</param>
/// <param name="TargetFiles">存在する処分の対象（本体・<c>-wal</c>・<c>-shm</c>）。</param>
/// <param name="TotalSizeBytes">対象の合計サイズ。</param>
/// <param name="Progress">実行中の進み具合（実行中でなければ <see langword="null"/>）。</param>
/// <param name="LastResult">このプロセスで直前に終わった処分の結果（再起動で消える）。</param>
/// <param name="ExecuteIdempotencyToken">次の <see cref="IOldDatabaseDisposalService.StartAsync"/> に渡す冪等トークン。</param>
/// <param name="MisplacedRecordedOldDatabasePath">
/// 切替の時点に記録した旧ファイルが、現在の設定から導いた対象とは別の名前で残っている場合のパス
/// （<c>Storage:SqliteFileName</c> を切替の後に変えた場合。製品はこのファイルを処分しない）。
/// </param>
public sealed record OldDatabaseDisposalStatus(
    OldDatabaseDisposalState State,
    IReadOnlyList<OldDatabaseFile> TargetFiles,
    long TotalSizeBytes,
    OldDatabaseDisposalProgress? Progress,
    OldDatabaseDisposalResult? LastResult,
    string? ExecuteIdempotencyToken,
    string? MisplacedRecordedOldDatabasePath = null);

/// <summary>処分の段階（進捗表示用）。</summary>
public enum OldDatabaseDisposalStage
{
    /// <summary>旧ファイルの WAL を本体へ反映している。</summary>
    Checkpointing,

    /// <summary>作業用サブフォルダへ書き出している。</summary>
    Exporting,

    /// <summary>管理者アカウント台帳と空き領域の残留を除いている。</summary>
    RemovingResidue,

    /// <summary>書き出したファイルを検証している。</summary>
    Verifying,

    /// <summary>改名と最終の ACL の適用。</summary>
    Finalizing,

    /// <summary>元のファイルを削除している（取り消せない）。</summary>
    Deleting,
}

/// <summary>実行中の処分の進み具合。</summary>
public sealed record OldDatabaseDisposalProgress(
    OldDatabaseDisposalMethod Method,
    OldDatabaseDisposalStage Stage,
    bool Cancellable,
    DateTimeOffset StartedAt);

/// <summary>対象の種類（安全検査 ①〜③ の結果）。</summary>
public enum OldDatabaseTargetKind
{
    /// <summary>本体が SQLite のデータベースファイル（どちらの方式でも処分できる）。</summary>
    SqliteDatabase,

    /// <summary>本体が 0 バイト（内容を持たない。削除でのみ処分する。確認は不要）。</summary>
    EmptyBody,

    /// <summary>本体の先頭の識別子が無い・壊れている（明示確認のうえ削除でのみ処分できる）。</summary>
    UnverifiableBody,

    /// <summary>本体が無く <c>-wal</c>・<c>-shm</c> だけが残る（削除でのみ処分する）。</summary>
    SidecarFilesOnly,

    /// <summary>安全検査を満たさない（処分を拒否する）。</summary>
    Unsafe,
}

/// <summary>移行の完了判定で「移行済み」とみなせなかった理由。</summary>
public enum OldDatabaseMigrationGap
{
    /// <summary>移行の完了記録が無い（未実行・中断・検証不合格）。</summary>
    NotCompleted,

    /// <summary>完了記録が位置を持たない（移行元 0 件で完了）のに、旧ファイルにレコードがある。</summary>
    CompletedWithoutPosition,

    /// <summary>移行の完了後に旧ファイルへ追加されたレコードがある。</summary>
    RecordsAddedAfterCompletion,

    /// <summary>時間帯ごとの照合で、移行先の件数が旧ファイルを下回る時間帯がある。</summary>
    HourlyReconciliationFailed,

    /// <summary>移行先のライブ受信と重なる区間（切替の反映より後）に旧ファイルのレコードがある。</summary>
    RecordsInUnreconcilableInterval,

    /// <summary>切替の境界の記録が無い・読めないため、照合できない区間を全区間とみなした。</summary>
    SwitchBoundaryUnknown,

    /// <summary>移行先が時間帯ごとの照合に対応していない。</summary>
    TargetCannotReconcile,

    /// <summary>旧ファイルのシステムイベント（受信できなかった時間帯の記録等）が移行先に無い。</summary>
    SystemEventsNotMigrated,
}

/// <summary>移行の完了判定（database.md §6.3「移行の完了判定と明示確認」）。</summary>
/// <param name="Migrated">移行済みとみなせるか。</param>
/// <param name="Gaps">移行済みとみなせなかった理由。</param>
/// <param name="UnmigratedRecordCount">未移行とみなしたレコードの件数。</param>
/// <param name="UnmigratedOldest">その ReceivedAt の最古。</param>
/// <param name="UnmigratedNewest">その ReceivedAt の最新。</param>
/// <param name="RecordsAfterSwitchCount">
/// 切替の反映より後の ReceivedAt を持つレコード（SQL Server に保存できなかった間に組み込み
/// データベースへ保存したログ）の件数。境界が分からない場合は 0。
/// </param>
/// <param name="RecordsAfterSwitchOldest">その最古。</param>
/// <param name="RecordsAfterSwitchNewest">その最新。</param>
/// <param name="UnmigratedSystemEventCount">移行先に無いシステムイベントの件数。</param>
/// <param name="UnmigratedSystemEventOldest">その StartAt の最古。</param>
/// <param name="UnmigratedSystemEventNewest">その EndAt の最新。</param>
public sealed record OldDatabaseMigrationJudgment(
    bool Migrated,
    IReadOnlyList<OldDatabaseMigrationGap> Gaps,
    long UnmigratedRecordCount,
    DateTimeOffset? UnmigratedOldest,
    DateTimeOffset? UnmigratedNewest,
    long RecordsAfterSwitchCount,
    DateTimeOffset? RecordsAfterSwitchOldest,
    DateTimeOffset? RecordsAfterSwitchNewest,
    long UnmigratedSystemEventCount,
    DateTimeOffset? UnmigratedSystemEventOldest,
    DateTimeOffset? UnmigratedSystemEventNewest)
{
    /// <summary>「移行せずに処分する」の明示確認が要るか。</summary>
    public bool RequiresConfirmation => !Migrated && (UnmigratedRecordCount > 0 || UnmigratedSystemEventCount > 0);
}

/// <summary>確認ダイアログの材料。</summary>
/// <param name="Status">検査の時点の状態。</param>
/// <param name="TargetKind">対象の種類。</param>
/// <param name="TargetRejectionReason">対象が <see cref="OldDatabaseTargetKind.Unsafe"/> の理由。</param>
/// <param name="ArchiveAllowed">書き出して保管を選べるか（本体が SQLite のデータベースのときだけ）。</param>
/// <param name="Migration">移行の完了判定（本体を読めた場合）。</param>
/// <param name="RecordCount">旧ファイルのログ件数。</param>
/// <param name="OldestReceivedAt">旧ファイルのログの ReceivedAt の最古。</param>
/// <param name="NewestReceivedAt">同、最新。</param>
/// <param name="SystemEventCount">旧ファイルのシステムイベントの件数。</param>
/// <param name="RetentionDays">現在のログ保存期間（日。無期限・不明は <see langword="null"/>）。</param>
/// <param name="RecordsBeyondRetentionCount">
/// 保存期間を過ぎたログの件数——書き出して保管ではこれも書き出したファイルに含まれる（画面で開示する）。
/// </param>
/// <param name="EstimatedArchiveBytes">書き出しに要る見込みの容量（本体 + <c>-wal</c>）。</param>
public sealed record OldDatabaseDisposalInspection(
    OldDatabaseDisposalStatus Status,
    OldDatabaseTargetKind TargetKind,
    string? TargetRejectionReason,
    bool ArchiveAllowed,
    OldDatabaseMigrationJudgment? Migration,
    long? RecordCount,
    DateTimeOffset? OldestReceivedAt,
    DateTimeOffset? NewestReceivedAt,
    long? SystemEventCount,
    int? RetentionDays,
    long? RecordsBeyondRetentionCount,
    long EstimatedArchiveBytes);

/// <summary>処分の要求。</summary>
/// <param name="Method">方式（既定値は持たない——呼び出し側が必ず指定する）。</param>
/// <param name="IdempotencyToken">状態に含まれる冪等トークン。</param>
/// <param name="ArchiveParentDirectory">書き出して保管の保管先（既存の親フォルダ）。削除では無視する。</param>
/// <param name="ConfirmDisposeWithoutMigration">「移行せずに処分する」の明示確認。</param>
/// <param name="ConfirmDeleteUnverifiableFile">識別子が壊れた本体を削除することの明示確認。</param>
/// <param name="OperatorAddress">操作者の接続元（監査記録用）。</param>
/// <param name="OperatorScheme">操作者の認証方式。</param>
/// <param name="OperatorPrincipal">操作者の認証済み利用者名。</param>
public sealed record OldDatabaseDisposalRequest(
    OldDatabaseDisposalMethod Method,
    string IdempotencyToken,
    string? ArchiveParentDirectory = null,
    bool ConfirmDisposeWithoutMigration = false,
    bool ConfirmDeleteUnverifiableFile = false,
    string? OperatorAddress = null,
    string? OperatorScheme = null,
    string? OperatorPrincipal = null);

/// <summary>処分の結果の分類。</summary>
public enum OldDatabaseDisposalOutcome
{
    /// <summary>検証を通過して実行を始めた（結果は状態で受け取る）。</summary>
    Started,

    /// <summary>完了した（対象をすべて削除した）。</summary>
    Completed,

    /// <summary>前提条件・対象の安全検査・保管先の条件で拒否した（何も変更していない）。</summary>
    Rejected,

    /// <summary>書き出して保管に失敗した（元のファイルは変更していない）。</summary>
    ArchiveFailed,

    /// <summary>削除の途中で失敗した（残ったファイルがある）。</summary>
    DeletePartiallyFailed,

    /// <summary>取り消し・サービスの停止で中断した。</summary>
    Interrupted,

    /// <summary>同じ冪等トークンで既に実行した（直前の結果を返す）。</summary>
    AlreadyApplied,

    /// <summary>冪等トークンが無効（状態を取り直してやり直す）。</summary>
    InvalidToken,
}

/// <summary>拒否・失敗の理由（画面の案内の分岐に使う）。</summary>
public enum OldDatabaseDisposalReason
{
    /// <summary>実効 provider が SQL Server でない。</summary>
    NotPromoted,

    /// <summary>処分の対象が無い。</summary>
    NoTargetFiles,

    /// <summary>移行の実行中。</summary>
    MigrationRunning,

    /// <summary>別の処分の実行中。</summary>
    DisposalRunning,

    /// <summary>対象の安全検査を満たさない。</summary>
    TargetUnsafe,

    /// <summary>この対象では書き出して保管を選べない。</summary>
    ArchiveNotAllowedForTarget,

    /// <summary>識別子が壊れた本体の削除の明示確認が無い。</summary>
    UnverifiableConfirmationRequired,

    /// <summary>「移行せずに処分する」の明示確認が無い。</summary>
    MigrationConfirmationRequired,

    /// <summary>受信が逼迫している（一時保管への退避の継続中・保存待ちの高水位）。</summary>
    LiveIngestionUnderPressure,

    /// <summary>
    /// 削除の前の監査記録（開始・削除の開始）が監査記録の正本に書けない（書けるまで処分を始めない・
    /// 削除の段階に入らない）。
    /// </summary>
    AuditUnavailable,

    /// <summary>旧ファイルを別のプログラムが使用中の可能性がある（WAL の反映が完了しない）。</summary>
    InUse,

    /// <summary>保管先が空・相対パス・ドライブ文字から始まらない。</summary>
    DestinationNotLocalAbsolutePath,

    /// <summary>保管先がネットワーク上の場所（UNC・デバイスパス・ネットワークドライブ）。</summary>
    DestinationNetwork,

    /// <summary>保管先が取り外せる媒体・光学ドライブ。</summary>
    DestinationRemovable,

    /// <summary>保管先が ACL を保持しないファイルシステム。</summary>
    DestinationNoAcl,

    /// <summary>保管先が存在しない（製品は親フォルダを作らない）。</summary>
    DestinationMissing,

    /// <summary>保管先がデータルート・一時保管の置き場所・インストール先の配下。</summary>
    DestinationInsideManagedArea,

    /// <summary>サービスの実行アカウントが保管先にフォルダを作れない。</summary>
    DestinationNotWritable,

    /// <summary>空き容量が足りない（開始前の見積もり・途中の再検査）。</summary>
    InsufficientFreeSpace,

    /// <summary>WAL の反映・書き出し・残留の除去のいずれかの失敗。</summary>
    ExportFailed,

    /// <summary>書き出したファイルの検証に不合格。</summary>
    VerificationFailed,

    /// <summary>改名・最終の ACL の適用に失敗。</summary>
    FinalizationFailed,

    /// <summary>削除に失敗したファイルがある。</summary>
    DeleteFailed,

    /// <summary>利用者が取り消した。</summary>
    Cancelled,

    /// <summary>サービスの停止要求で取り消された。</summary>
    ServiceStopping,

    /// <summary>想定外の失敗。</summary>
    Unexpected,
}

/// <summary>処分 1 回の結果（拒否を含む）。</summary>
/// <param name="Outcome">結果の分類。</param>
/// <param name="Message">結果の説明（画面に示す理由）。</param>
/// <param name="Reason">拒否・失敗の理由。</param>
/// <param name="Method">方式（拒否で方式が決まる前は <see langword="null"/>）。</param>
/// <param name="ArchiveFilePath">確定した書き出しファイルのパス。</param>
/// <param name="ArchiveSha256">その SHA-256（16 進小文字）。</param>
/// <param name="RemainingFiles">削除できずに残ったファイル。</param>
/// <param name="WorkDirectoryLeftBehind">削除できずに残った作業用サブフォルダ。</param>
/// <param name="GrantCommand">保管先にフォルダを作れないときの付与コマンド。</param>
/// <param name="RevokeCommand">処分の後に付与を外すコマンド。</param>
/// <param name="FinishedAt">結果の確定時刻。</param>
public sealed record OldDatabaseDisposalResult(
    OldDatabaseDisposalOutcome Outcome,
    string Message,
    OldDatabaseDisposalReason? Reason = null,
    OldDatabaseDisposalMethod? Method = null,
    string? ArchiveFilePath = null,
    string? ArchiveSha256 = null,
    IReadOnlyList<string>? RemainingFiles = null,
    string? WorkDirectoryLeftBehind = null,
    string? GrantCommand = null,
    string? RevokeCommand = null,
    DateTimeOffset? FinishedAt = null);

/// <summary>保管先の事前検査の結果。</summary>
/// <param name="Acceptable">条件を満たすか（フォルダを作れるかは実行時に確かめる）。</param>
/// <param name="Reason">満たさない理由。</param>
/// <param name="Message">画面に示す説明（次の一手を含む）。</param>
/// <param name="RequiredFreeBytes">必要な空き容量の見込み。</param>
/// <param name="AvailableFreeBytes">保管先ボリュームの空き容量。</param>
public sealed record OldDatabaseArchiveDestinationCheck(
    bool Acceptable,
    OldDatabaseDisposalReason? Reason,
    string Message,
    long? RequiredFreeBytes,
    long? AvailableFreeBytes);
