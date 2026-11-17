using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Observability.Auditing;
using Yagura.Storage;

namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>旧ファイルを開かずに観測した処分の事実（能動通知 1041・2031 の材料）。</summary>
/// <param name="Status">状態表の状態。</param>
/// <param name="EffectiveProviderIsSqlServer">実効 provider が SQL Server か。</param>
/// <param name="DisposedPathsInThisProcess">
/// このプロセスで処分の完了を記録した旧ファイル（本体のパス。<c>-wal</c>・<c>-shm</c> は本体に含める）。
/// </param>
/// <param name="UnresolvableReason">利用者の画面操作では解消できない理由（あれば）。</param>
public sealed record OldDatabaseDisposalObservation(
    OldDatabaseDisposalStatus Status,
    bool EffectiveProviderIsSqlServer,
    IReadOnlyCollection<string> DisposedPathsInThisProcess,
    string? UnresolvableReason);

/// <summary>旧ファイルを読む段（テストの差し込み口が読み取りの失敗を再現する位置）。</summary>
internal enum OldDatabaseReadStep
{
    /// <summary>要約（件数・期間・スキーマ）の読み取り。</summary>
    Summary,

    /// <summary>移行の完了判定のための照合。</summary>
    Reconciliation,
}

/// <summary>
/// 旧・組み込み DB ファイルの処分（database.md §6.3）の実体。
/// </summary>
/// <remarks>
/// <para>
/// <b>状態</b>: 処分の進み具合を記録するファイルは持たない。状態は残っているファイル（事実）と、このプロセスで
/// 保持する実行中の段階・直前の結果から決まる（直前の結果はサービスの再起動で消える）。
/// </para>
/// <para>
/// <b>実行の寿命</b>: 処分はホストの停止要求に結び付けた背景の処理として完走する（画面の circuit とは独立）。
/// 停止要求では削除の段階の前なら取り消す。削除の段階（ファイルの削除のみ）は最後まで行い、共有違反の
/// 再試行だけを打ち切る。
/// </para>
/// <para>
/// <b>排他</b>: 処分の検証から終了まで、旧ファイルの利用権（<see cref="OldDatabaseAccessGate"/>）を保持する。
/// </para>
/// <para>
/// <b>明示確認は照合の結果に結び付く</b>: 照合（<see cref="InspectAsync"/>）は明示確認の対象として示す要点の要約値を
/// 返し、要求はそれを運ぶ。開始時に照合し直した結果の要約値が一致しなければ、確認があっても開始を拒否する——
/// 照合の結果は画面に何時間でも残り得るため、利用者が見ていない件数のまま「確認あり」で処分しない。
/// </para>
/// <para>
/// <b>削除の前の監査記録は必須</b>: 開始の記録（削除のみの方式では削除の前の記録を兼ねる）と、書き出して保管の
/// 削除の開始の記録は、監査記録の正本に載ったことを確かめてから先へ進む（<see cref="IConfirmedAuditRecorder"/>）。
/// 載らなければ処分を始めない・削除の段階に入らない——処分は急ぐ理由のない不可逆な後始末であり、ADR-0004 決定 7
/// （監査記録の書き込み不能は要求処理を妨げない）の根拠である障害中の復旧操作にあたらない。
/// </para>
/// <para>スレッド安全。</para>
/// </remarks>
public sealed class OldDatabaseDisposalService : IOldDatabaseDisposalService, IAsyncDisposable
{
    /// <summary>作業用サブフォルダ名の接頭辞。</summary>
    public const string WorkDirectoryPrefix = OldDatabaseArchiveNaming.WorkDirectoryPrefix;

    private static readonly Regex WorkDirectoryNamePattern = new(
        @"^yagura-archive-\d{8}T\d{6}Z(-\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly OldDatabaseTargetPaths _targets;
    private readonly bool _effectiveProviderIsSqlServer;
    private readonly ILogStore _targetStore;
    private readonly OldDatabaseAccessGate _accessGate;
    private readonly IConfirmedAuditRecorder _auditRecorder;
    private readonly IArchivePlatform _platform;
    private readonly OldDatabaseDisposalEnvironment _environment;
    private readonly OldDatabaseDisposalOptions _options;
    private readonly IOldDatabaseFileDeleter _fileDeleter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OldDatabaseDisposalService> _logger;
    private readonly ArchiveDestinationValidator _destinationValidator;
    private readonly CancellationTokenSource _hostStopping = new();

    private readonly object _stateLock = new();
    private RunState? _running;
    private string? _startingToken;
    private OldDatabaseDisposalResult? _lastResult;
    private string _currentToken = NewToken();
    private string? _lastExecutedToken;
    private readonly HashSet<string> _disposedPaths = new(StringComparer.OrdinalIgnoreCase);

    public OldDatabaseDisposalService(
        OldDatabaseTargetPaths targets,
        bool effectiveProviderIsSqlServer,
        ILogStore targetStore,
        OldDatabaseAccessGate accessGate,
        IConfirmedAuditRecorder auditRecorder,
        IArchivePlatform platform,
        OldDatabaseDisposalEnvironment environment,
        OldDatabaseDisposalOptions? options = null,
        IOldDatabaseFileDeleter? fileDeleter = null,
        TimeProvider? timeProvider = null,
        ILogger<OldDatabaseDisposalService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(targetStore);
        ArgumentNullException.ThrowIfNull(accessGate);
        ArgumentNullException.ThrowIfNull(auditRecorder);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(environment);

        _targets = targets;
        _effectiveProviderIsSqlServer = effectiveProviderIsSqlServer;
        _targetStore = targetStore;
        _accessGate = accessGate;
        _auditRecorder = auditRecorder;
        _platform = platform;
        _environment = environment;
        _options = options ?? OldDatabaseDisposalOptions.Default;
        _fileDeleter = fileDeleter ?? new SystemOldDatabaseFileDeleter();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<OldDatabaseDisposalService>.Instance;
        _destinationValidator = new ArchiveDestinationValidator(platform, targets.DataRoot, environment, _options.FreeSpaceFloorBytes);
    }

    /// <summary>
    /// 処分が退避の失敗・削除の部分失敗・中断で終わったときに呼ばれる（能動通知 1041 の即時発火）。
    /// </summary>
    public Action<OldDatabaseDisposalResult, OldDatabaseDisposalObservation>? FailureFinalized { get; set; }

    /// <summary>実行中の処分（テストが完了を待つため）。</summary>
    internal Task? CurrentRun { get; private set; }

    /// <summary>
    /// 書き出しの直前に呼ばれる差し込み口（テストが取り消し・停止要求・途中の再検査を決定的に再現するため）。
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeExportForTest { get; set; }

    /// <summary>旧ファイルを読む直前に呼ばれる差し込み口（テストが使用中・一時的な読み取りの失敗を再現するため）。</summary>
    internal Action<OldDatabaseReadStep>? BeforeOldDatabaseReadForTest { get; set; }

    /// <summary>
    /// クラッシュ後の掃除で親フォルダを一覧する処理の差し替え口（テストが一覧できない親フォルダを再現するため）。
    /// </summary>
    internal Func<string, IEnumerable<string>>? ListWorkDirectoryCandidatesForTest { get; set; }

    /// <inheritdoc />
    public Task<OldDatabaseDisposalStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(BuildStatus());

    /// <summary>旧ファイルを開かずに観測する（能動通知の周期評価から呼ぶ）。</summary>
    public OldDatabaseDisposalObservation Observe()
    {
        var status = BuildStatus();
        string? unresolvable = null;
        if (status.State is OldDatabaseDisposalState.Pending or OldDatabaseDisposalState.LastAttemptFailed)
        {
            var inspection = OldDatabaseTargetInspector.Inspect(_targets);
            if (inspection.Kind == OldDatabaseTargetKind.Unsafe)
            {
                unresolvable = $"対象の安全検査を満たさないため、製品では後始末できません（{inspection.RejectionReason}）。";
            }
        }

        if (unresolvable is null && status.MisplacedRecordedOldDatabasePath is { } misplaced)
        {
            unresolvable =
                $"切り替えの時点の組み込みデータベースのファイル（{misplaced}）が、現在の設定（Storage:SqliteFileName）とは" +
                "別の名前で残っています。製品はこのファイルを後始末しません。";
        }

        string[] disposed;
        lock (_stateLock)
        {
            disposed = [.. _disposedPaths];
        }

        return new OldDatabaseDisposalObservation(status, _effectiveProviderIsSqlServer, disposed, unresolvable);
    }

    /// <inheritdoc />
    public async Task<OldDatabaseDisposalInspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        var status = BuildStatus();
        var bodyBytes = SizeOf(status, _targets.BodyPath);
        var walBytes = SizeOf(status, _targets.WalPath);
        var requiredArchiveFreeBytes = ArchiveDestinationValidator.EstimateRequiredBytes(bodyBytes, walBytes) + _options.FreeSpaceFloorBytes;

        // 対象外・実行中（移行・処分が排他を長く保持する）は旧ファイルを開かずに返す。
        if (status.State is not (OldDatabaseDisposalState.Pending or OldDatabaseDisposalState.LastAttemptFailed))
        {
            return EmptyInspection(status, OldDatabaseTargetInspector.Inspect(_targets).Kind, requiredArchiveFreeBytes);
        }

        // 照合の文も停止要求で止める（旧ファイル全体の読み取りが停止手順を待たせない）。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostStopping.Token);
        cancellationToken = linked.Token;

        using var lease = await _accessGate.TryAcquireAsync(
            OldDatabaseAccessHolder.Inspection, _options.GateWait, cancellationToken).ConfigureAwait(false);
        var targetInspection = OldDatabaseTargetInspector.Inspect(_targets);
        if (lease is null)
        {
            return EmptyInspection(BuildStatus(), targetInspection.Kind, requiredArchiveFreeBytes,
                unavailableReason: _accessGate.Holder == OldDatabaseAccessHolder.Migration
                    ? OldDatabaseDisposalReason.MigrationRunning
                    : OldDatabaseDisposalReason.DisposalRunning);
        }

        if (targetInspection.Kind != OldDatabaseTargetKind.SqliteDatabase)
        {
            return EmptyInspection(BuildStatus(), targetInspection.Kind, requiredArchiveFreeBytes, targetInspection.RejectionReason,
                confirmationDigest: ConfirmationDigestOf(targetInspection, targetInspection.Kind, null));
        }

        var read = await ReadContentAsync(cancellationToken).ConfigureAwait(false);
        if (read.Failure is OldDatabaseReadFailure.InUse or OldDatabaseReadFailure.Unreadable)
        {
            return EmptyInspection(BuildStatus(), OldDatabaseTargetKind.SqliteDatabase, requiredArchiveFreeBytes,
                unavailableReason: RejectionReasonOf(read.Failure.Value));
        }

        if (read.Kind != OldDatabaseTargetKind.SqliteDatabase)
        {
            return EmptyInspection(BuildStatus(), read.Kind, requiredArchiveFreeBytes, read.RejectionReason,
                confirmationDigest: ConfirmationDigestOf(targetInspection, read.Kind, null));
        }

        long? beyondRetention = null;
        var retentionDays = _environment.RetentionDays();
        if (retentionDays is { } days)
        {
            try
            {
                beyondRetention = await OldSqliteDatabase.CountBeforeAsync(
                    _targets.BodyPath, RetentionCutoff(days), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (OldSqliteDatabase.ClassifyReadFailure(ex) is not null && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(ex, "保存期間を過ぎたログの件数を数えられません。");
            }
        }

        return new OldDatabaseDisposalInspection(
            BuildStatus(),
            OldDatabaseTargetKind.SqliteDatabase,
            null,
            ArchiveAllowed: true,
            read.Judgment,
            read.Summary!.RecordCount,
            read.Summary.OldestReceivedAt,
            read.Summary.NewestReceivedAt,
            read.Summary.SystemEventCount,
            retentionDays,
            beyondRetention,
            requiredArchiveFreeBytes,
            ConfirmationDigest: ConfirmationDigestOf(targetInspection, OldDatabaseTargetKind.SqliteDatabase, read.Judgment));
    }

    /// <inheritdoc />
    public Task<OldDatabaseArchiveDestinationCheck> CheckArchiveDestinationAsync(
        string parentDirectory,
        CancellationToken cancellationToken = default)
    {
        var status = BuildStatus();
        var required = ArchiveDestinationValidator.EstimateRequiredBytes(
            SizeOf(status, _targets.BodyPath), SizeOf(status, _targets.WalPath));
        var validation = _destinationValidator.Validate(parentDirectory, required);
        return Task.FromResult(new OldDatabaseArchiveDestinationCheck(
            validation.Accepted,
            validation.Reason,
            validation.Message,
            required,
            validation.Destination?.Volume.AvailableFreeBytes));
    }

    /// <inheritdoc />
    public async Task<OldDatabaseDisposalResult> StartAsync(
        OldDatabaseDisposalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_stateLock)
        {
            if (_running is not null || _startingToken is not null)
            {
                var activeToken = _running?.IdempotencyToken ?? _startingToken;
                return string.Equals(activeToken, request.IdempotencyToken, StringComparison.Ordinal)
                    ? new OldDatabaseDisposalResult(OldDatabaseDisposalOutcome.AlreadyApplied, "後始末は既に実行中です。", Method: request.Method)
                    : Rejected(OldDatabaseDisposalReason.DisposalRunning, "別の後始末を実行中です。", request.Method);
            }

            if (_lastExecutedToken is not null
                && string.Equals(_lastExecutedToken, request.IdempotencyToken, StringComparison.Ordinal))
            {
                return (_lastResult ?? new OldDatabaseDisposalResult(OldDatabaseDisposalOutcome.Completed, "後始末は既に実行しました。"))
                    with { Outcome = OldDatabaseDisposalOutcome.AlreadyApplied };
            }

            if (!string.Equals(_currentToken, request.IdempotencyToken, StringComparison.Ordinal))
            {
                return new OldDatabaseDisposalResult(
                    OldDatabaseDisposalOutcome.InvalidToken,
                    "操作の有効期限が切れています。画面を更新してから、もう一度実行してください。",
                    Method: request.Method);
            }

            _startingToken = request.IdempotencyToken;
        }

        IDisposable? lease = null;
        try
        {
            if (!_effectiveProviderIsSqlServer)
            {
                return Rejected(OldDatabaseDisposalReason.NotPromoted, "保存先が SQL Server ではないため、後始末はできません。", request.Method);
            }

            // 移行は排他を長く保持するため待たずに拒否する。待つのは短い読み取りとの競合だけ。
            if (_accessGate.Holder == OldDatabaseAccessHolder.Migration)
            {
                return Rejected(OldDatabaseDisposalReason.MigrationRunning, "蓄積ログの移行を実行中のため、後始末を開始できません。", request.Method);
            }

            lease = await _accessGate.TryAcquireAsync(
                OldDatabaseAccessHolder.Disposal, _options.GateWait, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return _accessGate.Holder == OldDatabaseAccessHolder.Migration
                    ? Rejected(OldDatabaseDisposalReason.MigrationRunning, "蓄積ログの移行を実行中のため、後始末を開始できません。", request.Method)
                    : Rejected(OldDatabaseDisposalReason.DisposalRunning, "以前の保存先のファイルを別の操作が使用中です。しばらくしてから実行してください。", request.Method);
            }

            // 照合の文も停止要求で止める（旧ファイル全体の読み取りが停止手順を待たせない）。
            using var validation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostStopping.Token);
            var (plan, rejection) = await ValidateAsync(request, validation.Token).ConfigureAwait(false);
            if (rejection is not null)
            {
                return rejection;
            }

            if (!await RecordConfirmedAuditAsync(request, BuildStartDetail(plan!)).ConfigureAwait(false))
            {
                RemoveWorkDirectory(plan!);
                plan!.WorkDirectoryHold?.Dispose();
                return Rejected(OldDatabaseDisposalReason.AuditUnavailable,
                    "監査記録を書き込めないため、後始末を開始しませんでした（元のファイルは変更していません）。" +
                    "データフォルダの監査記録（audit フォルダ）に書き込めるか確かめてから、もう一度実行してください。",
                    request.Method);
            }

            RunState run;
            lock (_stateLock)
            {
                run = new RunState(request.IdempotencyToken, plan!.Method, _timeProvider.GetUtcNow(), _hostStopping.Token);
                _running = run;
                _lastExecutedToken = request.IdempotencyToken;
                _currentToken = NewToken();
            }

            var runLease = lease;
            lease = null;
            CurrentRun = Task.Run(() => RunAsync(plan!, run, runLease), CancellationToken.None);
            return new OldDatabaseDisposalResult(
                OldDatabaseDisposalOutcome.Started,
                "後始末を開始しました。画面を閉じても処理は続きます。",
                Method: plan!.Method);
        }
        finally
        {
            lease?.Dispose();
            lock (_stateLock)
            {
                _startingToken = null;
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> CancelAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_running is not { Cancellable: true } run)
            {
                return Task.FromResult(false);
            }

            run.CancelByUser();
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// サービスの停止要求に応じる: 削除の段階の前の処分を取り消し、終わるまで上限つきで待つ。
    /// </summary>
    public async Task StopAsync()
    {
        await _hostStopping.CancelAsync().ConfigureAwait(false);
        if (CurrentRun is { } run)
        {
            await Task.WhenAny(run, Task.Delay(_options.StopResponseTimeout, _timeProvider)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _hostStopping.Dispose();
    }

    // ---- 状態 ----

    private OldDatabaseDisposalStatus BuildStatus()
    {
        OldDatabaseDisposalResult? lastResult;
        RunState? running;
        string token;
        lock (_stateLock)
        {
            lastResult = _lastResult;
            running = _running;
            token = _currentToken;
        }

        if (!_effectiveProviderIsSqlServer)
        {
            return new OldDatabaseDisposalStatus(OldDatabaseDisposalState.NotApplicable, [], 0, null, lastResult, null);
        }

        var files = ListExistingTargets();
        var total = files.Sum(file => file.SizeBytes ?? 0);
        var misplaced = FindMisplacedRecordedOldDatabase();

        if (running is not null)
        {
            return new OldDatabaseDisposalStatus(
                running.Stage == OldDatabaseDisposalStage.Deleting
                    ? OldDatabaseDisposalState.DisposalDeleting
                    : OldDatabaseDisposalState.DisposalRunning,
                files,
                total,
                new OldDatabaseDisposalProgress(running.Method, running.Stage, running.Cancellable, running.StartedAt),
                lastResult,
                null,
                misplaced);
        }

        if (files.Count == 0)
        {
            return new OldDatabaseDisposalStatus(OldDatabaseDisposalState.NotApplicable, files, 0, null, lastResult, null, misplaced);
        }

        var state = _accessGate.Holder == OldDatabaseAccessHolder.Migration
            ? OldDatabaseDisposalState.MigrationRunning
            : IsFailure(lastResult)
                ? OldDatabaseDisposalState.LastAttemptFailed
                : OldDatabaseDisposalState.Pending;

        return new OldDatabaseDisposalStatus(
            state,
            files,
            total,
            null,
            lastResult,
            state is OldDatabaseDisposalState.Pending or OldDatabaseDisposalState.LastAttemptFailed ? token : null,
            misplaced);
    }

    private List<OldDatabaseFile> ListExistingTargets()
    {
        var files = new List<OldDatabaseFile>();
        foreach (var path in _targets.DeletionOrder)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    files.Add(new OldDatabaseFile(path, info.Length));
                }
                else if (info.LinkTarget is not null || Directory.Exists(path))
                {
                    files.Add(new OldDatabaseFile(path, null));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files.Add(new OldDatabaseFile(path, null));
            }
        }

        return files;
    }

    private string? FindMisplacedRecordedOldDatabase()
    {
        var boundary = PromotionSwitchBoundaryStore.TryRead(_targets.DataRoot);
        if (boundary is null)
        {
            return null;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            var recorded = Path.GetFullPath(boundary.OldDatabasePath);
            return !string.Equals(recorded, _targets.BodyPath, comparison) && File.Exists(recorded) ? recorded : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsFailure(OldDatabaseDisposalResult? result) =>
        result?.Outcome is OldDatabaseDisposalOutcome.ArchiveFailed
            or OldDatabaseDisposalOutcome.DeletePartiallyFailed
            or OldDatabaseDisposalOutcome.Interrupted;

    // ---- 検証（何も変更しない。最後の段で作業用サブフォルダを作る） ----

    private async Task<(DisposalPlan? Plan, OldDatabaseDisposalResult? Rejection)> ValidateAsync(
        OldDatabaseDisposalRequest request,
        CancellationToken cancellationToken)
    {
        var method = request.Method;
        var targetInspection = OldDatabaseTargetInspector.Inspect(_targets);
        if (targetInspection.NoTargets)
        {
            return (null, Rejected(OldDatabaseDisposalReason.NoTargetFiles, "以前の保存先のファイルは残っていません。", method));
        }

        if (targetInspection.Kind == OldDatabaseTargetKind.Unsafe)
        {
            await RecordSafetyRejectionAsync(request, targetInspection, targetInspection.RejectionReason!).ConfigureAwait(false);
            return (null, Rejected(OldDatabaseDisposalReason.TargetUnsafe,
                $"対象のファイルが安全検査を満たさないため、後始末を中止しました（{targetInspection.RejectionReason}）。", method));
        }

        // ライブ受信を優先する: 逼迫中は旧ファイル全体を読む照合でディスクと移行先を取り合わない。
        if (_environment.IsLiveIngestionUnderPressure())
        {
            return (null, Rejected(OldDatabaseDisposalReason.LiveIngestionUnderPressure,
                "ログの保存が受信に追いついていないため、後始末を開始しませんでした。受信の少ない時間帯に実行してください。", method));
        }

        var kind = targetInspection.Kind;
        OldDatabaseSummary? summary = null;
        OldDatabaseMigrationJudgment? judgment = null;
        if (kind == OldDatabaseTargetKind.SqliteDatabase)
        {
            var read = await ReadContentAsync(cancellationToken).ConfigureAwait(false);
            if (read.Kind == OldDatabaseTargetKind.Unsafe)
            {
                await RecordSafetyRejectionAsync(request, targetInspection, read.RejectionReason!).ConfigureAwait(false);
                return (null, Rejected(OldDatabaseDisposalReason.TargetUnsafe,
                    $"対象のファイルが安全検査を満たさないため、後始末を中止しました（{read.RejectionReason}）。", method));
            }

            if (read.Failure is OldDatabaseReadFailure.InUse or OldDatabaseReadFailure.Unreadable)
            {
                return (null, ReadFailureRejection(read.Failure.Value, method));
            }

            kind = read.Kind;
            summary = read.Summary;
            judgment = read.Judgment;
        }

        var digest = ConfirmationDigestOf(targetInspection, kind, judgment);
        var confirmedThisInspection = digest is not null
            && string.Equals(digest, request.ConfirmedInspectionDigest, StringComparison.Ordinal);

        if (method == OldDatabaseDisposalMethod.Archive && kind != OldDatabaseTargetKind.SqliteDatabase)
        {
            return (null, Rejected(OldDatabaseDisposalReason.ArchiveNotAllowedForTarget,
                "このファイルは内容を検証できないため、書き出して保管はできません。削除を選んでください。", method));
        }

        if (kind == OldDatabaseTargetKind.UnverifiableBody && !(request.ConfirmDeleteUnverifiableFile && confirmedThisInspection))
        {
            var body = targetInspection.ExistingFiles.First(file => file.Path == _targets.BodyPath);
            var fact = $"組み込みデータベースのファイルとして確認できないファイルです（{Path.GetFileName(body.Path)}、" +
                       $"{body.SizeBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"} バイト）。";
            return (null, Rejected(OldDatabaseDisposalReason.UnverifiableConfirmationRequired,
                request.ConfirmDeleteUnverifiableFile
                    ? fact + "照合の時点から対象が変わったため、もう一度照合してから確認してください。"
                    : fact + "削除してよいことを確認してください。",
                method));
        }

        if (judgment is { RequiresConfirmation: true } && !(request.ConfirmDisposeWithoutMigration && confirmedThisInspection))
        {
            var fact = $"まだ移行していないログが {judgment.UnmigratedRecordCount:N0} 件あります" +
                       $"{FormatRange(judgment.UnmigratedOldest, judgment.UnmigratedNewest)}";
            if (judgment.UnmigratedSystemEventCount > 0)
            {
                fact += $"（移行先に無いシステムイベント {judgment.UnmigratedSystemEventCount:N0} 件）";
            }

            return (null, Rejected(OldDatabaseDisposalReason.MigrationConfirmationRequired,
                request.ConfirmDisposeWithoutMigration
                    ? fact + "。照合の時点から結果が変わったため、もう一度照合してから確認してください。"
                    : fact + "。「移行せずに後始末する」を確認してください。",
                method));
        }

        ArchiveDestination? destination = null;
        string? workDirectory = null;
        IArchiveWorkDirectoryHold? hold = null;
        string? archiveFileName = null;
        (ArchiveVolumeInfo Volume, long Floor)? dataRootVolume = null;
        long? beyondRetention = null;
        if (method == OldDatabaseDisposalMethod.Archive)
        {
            // 保存期間を過ぎたログも書き出したファイルに含める（元のファイルそのものの保管が目的）。その件数を
            // 開始の記録で開示する。
            if (_environment.RetentionDays() is { } retentionDays)
            {
                try
                {
                    beyondRetention = await OldSqliteDatabase.CountBeforeAsync(
                        _targets.BodyPath, RetentionCutoff(retentionDays), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (OldSqliteDatabase.ClassifyReadFailure(ex) is { } readFailure
                    && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(ex, "保存期間を過ぎたログの件数を数えられません。");
                    return (null, ReadFailureRejection(
                        readFailure == OldDatabaseReadFailure.InUse ? readFailure : OldDatabaseReadFailure.Unreadable, method));
                }
            }

            var bodyBytes = targetInspection.ExistingFiles.FirstOrDefault(file => file.Path == _targets.BodyPath)?.SizeBytes ?? 0;
            var walBytes = targetInspection.ExistingFiles.FirstOrDefault(file => file.Path == _targets.WalPath)?.SizeBytes ?? 0;
            var validation = _destinationValidator.Validate(
                request.ArchiveParentDirectory,
                ArchiveDestinationValidator.EstimateRequiredBytes(bodyBytes, walBytes));
            if (!validation.Accepted)
            {
                return (null, Rejected(validation.Reason!.Value, validation.Message, method));
            }

            destination = validation.Destination!;

            // WAL の反映で本体が伸びる分（データルートのボリューム）も同じ下限で見積もる。
            dataRootVolume = _destinationValidator.DataRootVolume();
            if (dataRootVolume is { } dataVolume && dataVolume.Volume.AvailableFreeBytes - walBytes < dataVolume.Floor)
            {
                return (null, Rejected(OldDatabaseDisposalReason.InsufficientFreeSpace,
                    "データフォルダのドライブの空き容量が足りないため、後始末を開始しませんでした。", method));
            }

            var created = CreateWorkDirectory(destination.ParentDirectory, out var failure);
            if (created.Path is null)
            {
                return (null, created.Creation == WorkDirectoryCreation.AccessDenied
                    ? Rejected(OldDatabaseDisposalReason.DestinationNotWritable,
                        "サービスの実行アカウントが保管先にフォルダを作れません。次のコマンドを管理者として実行してから、" +
                        "もう一度実行してください（後始末の後は、続くコマンドで付与を外してください）。",
                        method) with
                    {
                        // フォルダの作成（AD）に加えて一覧（RD）——中断した処分の作業用サブフォルダの掃除が親フォルダを一覧する。
                        GrantCommand = $"icacls \"{destination.ParentDirectory}\" /grant \"{_platform.CurrentIdentityName}:(AD,RD)\"",
                        RevokeCommand = $"icacls \"{destination.ParentDirectory}\" /remove:g \"{_platform.CurrentIdentityName}\"",
                    }
                    : Rejected(OldDatabaseDisposalReason.DestinationNotWritable,
                        $"保管先に作業用のフォルダを作れませんでした（{failure}）。", method));
            }

            workDirectory = created.Path;

            // 処分の終了まで保持し、改名・差し替えを防ぐ。そのうえで作成の時点から保護 ACL が付いたことを確かめる
            // （ACL を保持しないボリュームでは、作成時に渡したセキュリティ記述子が黙って無視される）。
            hold = _platform.HoldWorkDirectory(workDirectory, out var holdFailure);
            if (hold is null)
            {
                RemoveUnheldWorkDirectory(workDirectory, recursive: false);
                return (null, Rejected(OldDatabaseDisposalReason.DestinationNotWritable,
                    $"保管先に作った作業用のフォルダを確保できませんでした（{holdFailure}）。", method));
            }

            if (!_platform.HasWorkInProgressAcl(workDirectory))
            {
                hold.Dispose();
                RemoveUnheldWorkDirectory(workDirectory, recursive: false);
                return (null, Rejected(OldDatabaseDisposalReason.DestinationNoAcl,
                    "保管先に作った作業用のフォルダに保護のアクセス権（ACL）が付きませんでした。アクセス権を保持する NTFS の" +
                    "ドライブを指定してください（元のファイルは変更していません）。", method));
            }

            archiveFileName = Path.GetFileName(workDirectory) + OldDatabaseArchiveNaming.FileExtension;
        }

        return (new DisposalPlan(
            method,
            kind,
            targetInspection,
            summary,
            judgment,
            request,
            destination,
            dataRootVolume?.Floor,
            workDirectory,
            archiveFileName,
            beyondRetention)
        {
            WorkDirectoryHold = hold,
        }, null);
    }

    private (string? Path, WorkDirectoryCreation Creation) CreateWorkDirectory(string parent, out string? failure)
    {
        var baseName = WorkDirectoryPrefix + _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        failure = null;
        for (var attempt = 1; attempt <= 9; attempt++)
        {
            var name = attempt == 1 ? baseName : $"{baseName}-{attempt}";
            var path = Path.Combine(parent, name);
            var creation = _platform.CreateProtectedWorkDirectory(path, out failure);
            if (creation == WorkDirectoryCreation.Created)
            {
                return (path, creation);
            }

            if (creation != WorkDirectoryCreation.AlreadyExists)
            {
                return (null, creation);
            }
        }

        return (null, WorkDirectoryCreation.AlreadyExists);
    }

    /// <summary>
    /// クラッシュ後の掃除: 命名規則に一致し、かつ ACL が製品の作業中の構成と一致するサブフォルダだけを削除する
    /// （確定の前に止まったもの——元のファイルは変更されていない）。名前の一致だけでは削除しない。
    /// </summary>
    private (IReadOnlyList<string> Removed, bool Skipped) CleanUpAbandonedWorkDirectories(string parent, string currentWorkDirectory)
    {
        var removed = new List<string>();
        IEnumerable<string> candidates;
        try
        {
            candidates = (ListWorkDirectoryCandidatesForTest?.Invoke(parent)
                ?? Directory.EnumerateDirectories(parent, WorkDirectoryPrefix + "*")).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一覧できなければ掃除は見送り、残っているかもしれないことを結果と終了の記録で利用者に伝える。
            _logger.LogWarning(ex, "保管先の親フォルダ {Parent} を一覧できないため、中断した後始末の作業用フォルダの掃除を見送ります。", parent);
            return (removed, true);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (string.Equals(candidate, currentWorkDirectory, StringComparison.Ordinal)
                    || !WorkDirectoryNamePattern.IsMatch(Path.GetFileName(candidate))
                    || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0
                    || !_platform.HasWorkInProgressAcl(candidate))
                {
                    continue;
                }

                Directory.Delete(candidate, recursive: true);
                removed.Add(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogInformation(ex, "中断した後始末の作業用フォルダ {Directory} を削除できませんでした。", candidate);
            }
        }

        return (removed, false);
    }

    /// <summary>旧ファイルの内容を読み、スキーマを確かめ、移行の完了を判定する（排他の下で呼ぶ）。</summary>
    private async Task<ContentRead> ReadContentAsync(CancellationToken cancellationToken)
    {
        OldDatabaseSummary summary;
        try
        {
            BeforeOldDatabaseReadForTest?.Invoke(OldDatabaseReadStep.Summary);
            summary = await OldSqliteDatabase.ReadSummaryAsync(_targets.BodyPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("照合を取り消しました。", ex, cancellationToken);
        }
        catch (Exception ex) when (OldSqliteDatabase.ClassifyReadFailure(ex) is { } failure)
        {
            return ReadFailed(failure, ex);
        }

        if (!summary.HasLogTable)
        {
            return new ContentRead(OldDatabaseTargetKind.Unsafe, "組み込みデータベースのスキーマ（ログ本体の表）を持たないファイルです", null, null);
        }

        var retentionDays = _environment.RetentionDays();
        OldDatabaseMigrationJudgment judgment;
        try
        {
            BeforeOldDatabaseReadForTest?.Invoke(OldDatabaseReadStep.Reconciliation);
            judgment = await MigrationCompletionJudge.JudgeAsync(
                _targets.BodyPath,
                summary,
                LogMigrationService.ReadCheckpointState(_targets.DataRoot),
                PromotionSwitchBoundaryStore.TryRead(_targets.DataRoot),
                _targetStore,
                retentionDays is { } days ? RetentionCutoff(days) : null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("照合を取り消しました。", ex, cancellationToken);
        }
        catch (Exception ex) when (OldSqliteDatabase.ClassifyReadFailure(ex) is { } failure)
        {
            return ReadFailed(failure, ex);
        }
        catch (MigrationTargetUnavailableException ex)
        {
            // 移行先へ問い合わせられない（SQL Server の一時的な障害等）: 照合できないものとして、すべてを
            // 未移行とみなす（安全側——明示確認に回す）。
            _logger.LogInformation(ex, "移行先へ問い合わせられないため、移行の完了を確かめられません。");
            judgment = new OldDatabaseMigrationJudgment(
                Migrated: false,
                Gaps: [OldDatabaseMigrationGap.TargetCannotReconcile],
                UnmigratedRecordCount: summary.RecordCount,
                UnmigratedOldest: summary.OldestReceivedAt,
                UnmigratedNewest: summary.NewestReceivedAt,
                RecordsAfterSwitchCount: 0,
                RecordsAfterSwitchOldest: null,
                RecordsAfterSwitchNewest: null,
                UnmigratedSystemEventCount: summary.SystemEventCount,
                UnmigratedSystemEventOldest: null,
                UnmigratedSystemEventNewest: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 分類できない失敗は、何も変更せずに中止する側へ倒す。
            return ReadFailed(OldDatabaseReadFailure.Unreadable, ex);
        }

        return new ContentRead(OldDatabaseTargetKind.SqliteDatabase, null, summary, judgment);
    }

    /// <summary>
    /// 読み取りの失敗の扱い: 破損（SQLite がそう報告したもの）だけを内容を検証できない対象とし、使用中・その他の失敗は
    /// 何も変更せずに拒否する材料にする。
    /// </summary>
    private ContentRead ReadFailed(OldDatabaseReadFailure failure, Exception exception)
    {
        if (failure == OldDatabaseReadFailure.Corrupt)
        {
            _logger.LogInformation(exception, "以前の保存先のファイルが破損しているため、内容を読めません。");
            return new ContentRead(OldDatabaseTargetKind.UnverifiableBody, null, null, null);
        }

        _logger.LogWarning(exception, "以前の保存先のファイルを読めません（{Failure}）。何も変更せずに中止します。", failure);
        return new ContentRead(OldDatabaseTargetKind.SqliteDatabase, null, null, null, failure);
    }

    private static OldDatabaseDisposalReason RejectionReasonOf(OldDatabaseReadFailure failure) =>
        failure == OldDatabaseReadFailure.InUse ? OldDatabaseDisposalReason.InUse : OldDatabaseDisposalReason.TargetUnreadable;

    private static OldDatabaseDisposalResult ReadFailureRejection(OldDatabaseReadFailure failure, OldDatabaseDisposalMethod method) =>
        failure == OldDatabaseReadFailure.InUse
            ? Rejected(OldDatabaseDisposalReason.InUse,
                "別のプログラム（バックアップソフト・ウイルス対策・データベースの閲覧ツール等）が以前の保存先のファイルを使用して" +
                "いる可能性があるため、後始末を開始しませんでした（元のファイルは変更していません）。閉じてから、もう一度実行してください。",
                method)
            : Rejected(OldDatabaseDisposalReason.TargetUnreadable,
                "以前の保存先のファイルを一時的に読めないため、後始末を開始しませんでした（元のファイルは変更していません）。" +
                "しばらくしてから、もう一度実行してください。",
                method);

    /// <summary>
    /// 明示確認の対象として利用者に示す照合の要点の要約値。明示確認が要らなければ <see langword="null"/>。
    /// </summary>
    private string? ConfirmationDigestOf(
        OldDatabaseTargetInspection targetInspection,
        OldDatabaseTargetKind kind,
        OldDatabaseMigrationJudgment? judgment)
    {
        var basis = new StringBuilder();
        if (kind == OldDatabaseTargetKind.UnverifiableBody
            && targetInspection.ExistingFiles.FirstOrDefault(file => file.Path == _targets.BodyPath) is { } body)
        {
            basis.Append(CultureInfo.InvariantCulture, $"unverifiable|{Path.GetFileName(body.Path)}|{body.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        }
        else if (judgment is { RequiresConfirmation: true })
        {
            basis.Append(CultureInfo.InvariantCulture,
                $"unmigrated|{judgment.UnmigratedRecordCount}|{Ticks(judgment.UnmigratedOldest)}|{Ticks(judgment.UnmigratedNewest)}" +
                $"|events|{judgment.UnmigratedSystemEventCount}|{Ticks(judgment.UnmigratedSystemEventOldest)}|{Ticks(judgment.UnmigratedSystemEventNewest)}" +
                $"|afterSwitch|{judgment.RecordsAfterSwitchCount}|{Ticks(judgment.RecordsAfterSwitchOldest)}|{Ticks(judgment.RecordsAfterSwitchNewest)}" +
                $"|gaps|{string.Join(",", judgment.Gaps.Distinct().Order())}");
        }
        else
        {
            return null;
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(basis.ToString())));

        static string Ticks(DateTimeOffset? value) => value?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-";
    }

    private DateTimeOffset RetentionCutoff(int days) => _timeProvider.GetUtcNow().AddDays(-days);

    // ---- 実行 ----

    private async Task RunAsync(DisposalPlan plan, RunState run, IDisposable lease)
    {
        OldDatabaseDisposalResult result;
        try
        {
            result = plan.Method == OldDatabaseDisposalMethod.Archive
                ? await RunArchiveAsync(plan, run).ConfigureAwait(false)
                : await RunDeleteAsync(plan, run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "以前の保存先のファイルの後始末で想定外の失敗が起きました。");
            result = new OldDatabaseDisposalResult(
                run.Stage == OldDatabaseDisposalStage.Deleting ? OldDatabaseDisposalOutcome.DeletePartiallyFailed : OldDatabaseDisposalOutcome.Interrupted,
                $"後始末を完了できませんでした（{ex.Message}）。",
                OldDatabaseDisposalReason.Unexpected,
                plan.Method,
                RemainingFiles: ListExistingTargets().Select(file => file.Path).ToList(),
                // 確定済みの書き出しファイルは消さない（確定の前なら作業用サブフォルダごと片付ける）。
                WorkDirectoryLeftBehind: plan.Finalized ? null : RemoveWorkDirectory(plan));
        }
        finally
        {
            plan.WorkDirectoryHold?.Dispose();
        }

        result = result with
        {
            FinishedAt = _timeProvider.GetUtcNow(),
            UncheckedArchiveParentDirectory = plan.CleanupSkipped ? plan.Destination?.ParentDirectory : null,
        };
        await RecordAuditAsync(plan.Request, BuildEndDetail(plan, result)).ConfigureAwait(false);

        lock (_stateLock)
        {
            _running = null;
            _lastResult = result;
            if (result.Outcome == OldDatabaseDisposalOutcome.Completed)
            {
                // 本体のパスで覚える（-wal・-shm は本体と同じファイルとして扱う——能動通知の 2031 の判定と同じ単位）。
                _disposedPaths.Add(_targets.BodyPath);
            }
        }

        lease.Dispose();
        run.Dispose();

        if (IsFailure(result) && FailureFinalized is { } notify)
        {
            try
            {
                notify(result, Observe());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "後始末の失敗の通知に失敗しました。");
            }
        }
    }

    /// <summary>
    /// 取り消せない状態へ移る。利用者の取り消し・停止要求が既に届いていれば移らずに <see langword="false"/>
    /// （取り消しの受け付けと同じロックの下で判定し、受け付けた取り消しを黙って無視しない）。
    /// </summary>
    private bool TryMakeNonCancellable(RunState run)
    {
        lock (_stateLock)
        {
            if (run.Token.IsCancellationRequested)
            {
                return false;
            }

            run.MakeNonCancellable();
            return true;
        }
    }

    /// <summary>作業用サブフォルダが保持した場所から動いていないこと（パスで書く・確定する前に確かめる）。</summary>
    private static bool WorkDirectoryStillInPlace(DisposalPlan plan) => plan.WorkDirectoryHold?.StillInPlace() ?? false;

    private const string WorkDirectoryMovedMessage =
        "保管用の作業フォルダが別の場所に移された・差し替えられた可能性があるため、取り消しました。" +
        "保管先の親フォルダを、管理者以外が変更できない場所にしてから、もう一度実行してください。";

    private async Task<OldDatabaseDisposalResult> RunArchiveAsync(DisposalPlan plan, RunState run)
    {
        var workDirectory = plan.WorkDirectory!;
        var finalPath = Path.Combine(workDirectory, plan.ArchiveFileName!);
        ArchiveExportResult export;

        // クラッシュ後の掃除は開始の記録の後に行う（最初の変更の前に開始を記録する）。
        (plan.CleanedUpWorkDirectories, plan.CleanupSkipped) =
            CleanUpAbandonedWorkDirectories(plan.Destination!.ParentDirectory, workDirectory);

        using (var watchdogCts = new CancellationTokenSource())
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token, watchdogCts.Token))
        {
            var token = linked.Token;
            Task? watchdog = null;
            try
            {
                run.SetStage(OldDatabaseDisposalStage.Checkpointing);
                var checkpoint = await OldSqliteDatabase.CheckpointAsync(_targets.BodyPath, token).ConfigureAwait(false);
                if (checkpoint != OldDatabaseCheckpointOutcome.Completed)
                {
                    return ArchiveFailed(plan, checkpoint == OldDatabaseCheckpointOutcome.Busy
                        ? OldDatabaseDisposalReason.InUse
                        : OldDatabaseDisposalReason.ExportFailed,
                        checkpoint == OldDatabaseCheckpointOutcome.Busy
                            ? "別のプログラムが以前の保存先のファイルを使用している可能性があります。閉じてから、もう一度実行してください。"
                            : "以前の保存先のファイルが破損しているため、書き出せません。削除を選ぶか、利用者向けドキュメントの手順で扱ってください。");
                }

                watchdog = RunWatchdogAsync(plan, run, watchdogCts, token);
                if (BeforeExportForTest is { } beforeExport)
                {
                    await beforeExport(token).ConfigureAwait(false);
                }

                // 各段は作業用サブフォルダの中のファイルをパスで開く。開く前に、フォルダが保持した場所のままか確かめる。
                export = await SqliteArchiveExporter.ExportAsync(
                    _targets.BodyPath,
                    workDirectory,
                    stage =>
                    {
                        if (!WorkDirectoryStillInPlace(plan))
                        {
                            throw new ArchiveExportException(OldDatabaseDisposalReason.ExportFailed, WorkDirectoryMovedMessage);
                        }

                        run.SetStage(stage);
                    },
                    token).ConfigureAwait(false);

                run.SetStage(OldDatabaseDisposalStage.Finalizing);
                token.ThrowIfCancellationRequested();
                if (!TryMakeNonCancellable(run))
                {
                    throw new OperationCanceledException(token);
                }
            }
            catch (OperationCanceledException)
            {
                return run.AbortReason is { } abort
                    ? ArchiveFailed(plan, abort, abort == OldDatabaseDisposalReason.InsufficientFreeSpace
                        ? "書き出しの途中で空き容量が下限を下回ったため、取り消しました。"
                        : "書き出しの途中でログの保存が受信に追いつかなくなったため、取り消しました。受信の少ない時間帯に実行してください。")
                    : Interrupted(plan, run.StoppedByHost ? OldDatabaseDisposalReason.ServiceStopping : OldDatabaseDisposalReason.Cancelled);
            }
            catch (ArchiveExportException ex)
            {
                return ArchiveFailed(plan, ex.Reason, ex.Message);
            }
            finally
            {
                await watchdogCts.CancelAsync().ConfigureAwait(false);
                if (watchdog is not null)
                {
                    await watchdog.ConfigureAwait(false);
                }
            }
        }

        try
        {
            if (!WorkDirectoryStillInPlace(plan))
            {
                return ArchiveFailed(plan, OldDatabaseDisposalReason.FinalizationFailed, WorkDirectoryMovedMessage);
            }

            File.Move(export.ExportedFilePath, finalPath, overwrite: false);
            _platform.ApplyFinalAcl(workDirectory, finalPath);
            if (!_platform.HasFinalAcl(workDirectory, finalPath) || !WorkDirectoryStillInPlace(plan))
            {
                throw new InvalidOperationException("最終のアクセス権を確認できません。");
            }

            plan.Finalized = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return ArchiveFailed(plan, OldDatabaseDisposalReason.FinalizationFailed,
                $"書き出したファイルの確定（名前の変更・アクセス権の適用）に失敗しました: {ex.Message}");
        }

        // 停止要求では削除の段階の前なら取り消す（確定済みの書き出しファイルは残り、結果から辿れる）。
        if (run.StoppedByHost)
        {
            return new OldDatabaseDisposalResult(
                OldDatabaseDisposalOutcome.Interrupted,
                "サービスの停止のため、元のファイルの削除を始めませんでした（元のファイルは変更していません）。" +
                $"書き出したファイル（{finalPath}）は確定済みです。",
                OldDatabaseDisposalReason.ServiceStopping,
                plan.Method,
                ArchiveFilePath: finalPath,
                ArchiveSha256: export.Sha256);
        }

        if (!await RecordConfirmedAuditAsync(plan.Request, BuildDeletionStartDetail(plan, finalPath, export)).ConfigureAwait(false))
        {
            // 確定済みの書き出しファイルは製品からはもう到達できない（最終の ACL）。残して結果から辿れるようにする。
            return new OldDatabaseDisposalResult(
                OldDatabaseDisposalOutcome.Interrupted,
                "監査記録を書き込めないため、元のファイルの削除を始めませんでした（元のファイルは変更していません）。" +
                $"書き出したファイル（{finalPath}）は確定済みです。データフォルダの監査記録（audit フォルダ）に" +
                "書き込めるか確かめてから、もう一度実行してください。",
                OldDatabaseDisposalReason.AuditUnavailable,
                plan.Method,
                ArchiveFilePath: finalPath,
                ArchiveSha256: export.Sha256);
        }

        var deletion = await DeleteTargetsAsync(run).ConfigureAwait(false);
        if (deletion.Remaining.Count > 0)
        {
            return new OldDatabaseDisposalResult(
                OldDatabaseDisposalOutcome.DeletePartiallyFailed,
                PartialDeletionMessage(deletion) + $" 書き出したファイル（{finalPath}）は確定済みです。",
                OldDatabaseDisposalReason.DeleteFailed,
                plan.Method,
                ArchiveFilePath: finalPath,
                ArchiveSha256: export.Sha256,
                RemainingFiles: deletion.Remaining);
        }

        return Completed(plan, "以前の保存先のファイルを書き出して保管し、元のファイルを削除しました。", finalPath, export.Sha256);
    }

    private async Task<OldDatabaseDisposalResult> RunDeleteAsync(DisposalPlan plan, RunState run)
    {
        // 先頭の識別子を持たない・壊れた本体でも反映を試みる——使用中なら止め、破損による失敗なら省いて進む。
        if (plan.Kind is OldDatabaseTargetKind.SqliteDatabase or OldDatabaseTargetKind.UnverifiableBody)
        {
            try
            {
                run.SetStage(OldDatabaseDisposalStage.Checkpointing);
                var checkpoint = await OldSqliteDatabase.CheckpointAsync(_targets.BodyPath, run.Token).ConfigureAwait(false);
                if (checkpoint == OldDatabaseCheckpointOutcome.Busy)
                {
                    return new OldDatabaseDisposalResult(
                        OldDatabaseDisposalOutcome.Interrupted,
                        "別のプログラムが以前の保存先のファイルを使用している可能性があるため、削除を開始しませんでした" +
                        "（元のファイルは変更していません）。閉じてから、もう一度実行してください。",
                        OldDatabaseDisposalReason.InUse,
                        plan.Method);
                }

                // 破損による反映の失敗は削除を止める理由にならない（破損した旧ファイルは残置の理由にならない）。
                run.Token.ThrowIfCancellationRequested();
                if (!TryMakeNonCancellable(run))
                {
                    throw new OperationCanceledException(run.Token);
                }
            }
            catch (OperationCanceledException)
            {
                return Interrupted(plan, run.StoppedByHost ? OldDatabaseDisposalReason.ServiceStopping : OldDatabaseDisposalReason.Cancelled);
            }
        }
        else if (!TryMakeNonCancellable(run))
        {
            return Interrupted(plan, run.StoppedByHost ? OldDatabaseDisposalReason.ServiceStopping : OldDatabaseDisposalReason.Cancelled);
        }

        var deletion = await DeleteTargetsAsync(run).ConfigureAwait(false);
        if (deletion.Remaining.Count > 0)
        {
            return new OldDatabaseDisposalResult(
                OldDatabaseDisposalOutcome.DeletePartiallyFailed,
                PartialDeletionMessage(deletion),
                OldDatabaseDisposalReason.DeleteFailed,
                plan.Method,
                RemainingFiles: deletion.Remaining);
        }

        return Completed(plan, "以前の保存先のファイルを削除しました。", null, null);
    }

    /// <summary>
    /// 書き出し・検証の途中の再検査: 空き容量が下限を下回る・受信が逼迫したら取り消す（退避の失敗として扱う）。
    /// </summary>
    private async Task RunWatchdogAsync(
        DisposalPlan plan,
        RunState run,
        CancellationTokenSource abort,
        CancellationToken runToken)
    {
        try
        {
            while (!runToken.IsCancellationRequested)
            {
                await Task.Delay(_options.WatchdogInterval, _timeProvider, runToken).ConfigureAwait(false);

                OldDatabaseDisposalReason? reason = null;
                if (plan.Destination is { } destination
                    && _platform.GetVolume(destination.ParentDirectory) is { } volume
                    && volume.AvailableFreeBytes < destination.FloorBytes)
                {
                    reason = OldDatabaseDisposalReason.InsufficientFreeSpace;
                }
                else if (plan.DataRootFloorBytes is { } dataFloor
                    && _platform.GetVolume(_targets.DataRoot) is { } dataVolume
                    && dataVolume.AvailableFreeBytes < dataFloor)
                {
                    reason = OldDatabaseDisposalReason.InsufficientFreeSpace;
                }
                else if (_environment.IsLiveIngestionUnderPressure())
                {
                    reason = OldDatabaseDisposalReason.LiveIngestionUnderPressure;
                }

                if (reason is not null)
                {
                    run.AbortReason = reason;
                    await abort.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 書き出しの終了・取り消しで止まった。
        }
    }

    /// <summary>
    /// <c>-wal</c>・<c>-shm</c> を先に、本体を最後に削除する。1 つでも失敗したらその時点で止める。
    /// 共有違反は短く再試行し、停止要求では再試行だけを打ち切る。
    /// </summary>
    private async Task<DeletionOutcome> DeleteTargetsAsync(RunState run)
    {
        run.SetStage(OldDatabaseDisposalStage.Deleting);
        var order = _targets.DeletionOrder;
        for (var index = 0; index < order.Count; index++)
        {
            var path = order[index];
            if (!File.Exists(path))
            {
                continue;
            }

            var deadline = _timeProvider.GetUtcNow() + _options.DeleteRetryBudget;
            while (true)
            {
                try
                {
                    _fileDeleter.Delete(path);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (OldDatabaseFileErrors.IsSharingViolation(ex)
                        && _timeProvider.GetUtcNow() < deadline
                        && !run.StoppedByHost)
                    {
                        try
                        {
                            await Task.Delay(_options.DeleteRetryInterval, _timeProvider, run.HostStoppingToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // 停止要求では再試行だけを打ち切る。
                        }

                        continue;
                    }

                    var remaining = order.Skip(index).Where(File.Exists).ToList();
                    return new DeletionOutcome(remaining, ex.Message);
                }
            }
        }

        return new DeletionOutcome([], null);
    }

    private OldDatabaseDisposalResult Completed(DisposalPlan plan, string message, string? archivePath, string? sha256)
    {
        string checkpointSummary;
        try
        {
            var removed = LogMigrationService.RemoveCheckpoint(_targets.DataRoot);
            checkpointSummary = removed is null
                ? "なし"
                : $"撤去（完了記録={(removed.Completed ? "あり" : "なし")}、移行件数={removed.MigratedCount}）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            checkpointSummary = $"撤去に失敗（{ex.Message}）";
            _logger.LogWarning(ex, "移行チェックポイントを撤去できませんでした。");
        }

        RemoveSwitchBoundaryIfItNamesTheDisposedFile();
        plan.CheckpointSummary = checkpointSummary;
        return new OldDatabaseDisposalResult(
            OldDatabaseDisposalOutcome.Completed,
            message,
            null,
            plan.Method,
            ArchiveFilePath: archivePath,
            ArchiveSha256: sha256,
            RemainingFiles: []);
    }

    private void RemoveSwitchBoundaryIfItNamesTheDisposedFile()
    {
        var boundary = PromotionSwitchBoundaryStore.TryRead(_targets.DataRoot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (boundary is null || !string.Equals(Path.GetFullPath(boundary.OldDatabasePath), _targets.BodyPath, comparison))
        {
            return;
        }

        try
        {
            File.Delete(Path.Combine(_targets.DataRoot, PromotionSwitchBoundaryStore.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "切替の境界の記録を撤去できませんでした。");
        }
    }

    private OldDatabaseDisposalResult ArchiveFailed(DisposalPlan plan, OldDatabaseDisposalReason reason, string detail)
    {
        var leftBehind = RemoveWorkDirectory(plan);
        return new OldDatabaseDisposalResult(
            OldDatabaseDisposalOutcome.ArchiveFailed,
            $"書き出して保管できませんでした。元のファイルは変更していません。（{detail}）",
            reason,
            plan.Method,
            WorkDirectoryLeftBehind: leftBehind);
    }

    private OldDatabaseDisposalResult Interrupted(DisposalPlan plan, OldDatabaseDisposalReason reason)
    {
        var leftBehind = RemoveWorkDirectory(plan);
        return new OldDatabaseDisposalResult(
            OldDatabaseDisposalOutcome.Interrupted,
            reason == OldDatabaseDisposalReason.ServiceStopping
                ? "サービスの停止のため、後始末を取り消しました。元のファイルは変更していません。"
                : "後始末を取り消しました。元のファイルは変更していません。",
            reason,
            plan.Method,
            WorkDirectoryLeftBehind: leftBehind);
    }

    /// <summary>
    /// 作業用サブフォルダを中身ごと削除する。削除できなければそのパスを返す。保持した場所から動いていれば、パスは
    /// もう製品のフォルダを指さないため削除せず、フォルダの今の場所を返す（作成時点からの保護 ACL のまま残る）。
    /// </summary>
    private string? RemoveWorkDirectory(DisposalPlan plan)
    {
        if (plan.WorkDirectory is not { } workDirectory)
        {
            return null;
        }

        if (plan.WorkDirectoryHold is { } hold)
        {
            if (!hold.StillInPlace())
            {
                var current = hold.CurrentPath() ?? workDirectory;
                _logger.LogWarning(
                    "後始末の作業用フォルダが {Original} から {Current} へ移された可能性があるため、パスによる削除を行いません。",
                    workDirectory,
                    current);
                return current;
            }

            // 自身の保持（削除の共有を許さない）を手放してから削除する。
            hold.Dispose();
        }

        return RemoveUnheldWorkDirectory(workDirectory, recursive: true);
    }

    /// <param name="workDirectory">作業用サブフォルダ。</param>
    /// <param name="recursive">
    /// 中身ごと消すか。まだ何も書いていない（保持・保護を確かめられなかった）フォルダは中身ごとは消さない——
    /// 差し替えられた先の中身を消さないため。
    /// </param>
    private string? RemoveUnheldWorkDirectory(string workDirectory, bool recursive)
    {
        if (!Directory.Exists(workDirectory))
        {
            return null;
        }

        try
        {
            Directory.Delete(workDirectory, recursive);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "後始末の作業用フォルダ {Directory} を削除できませんでした（作成時点からの保護 ACL のまま残ります）。", workDirectory);
            return workDirectory;
        }
    }

    private static string PartialDeletionMessage(DeletionOutcome deletion) =>
        $"元のファイルの一部を削除できませんでした（{string.Join("、", deletion.Remaining.Select(Path.GetFileName))}）。" +
        "別のプログラム（バックアップソフト・ウイルス対策・データベースの閲覧ツール等）が開いている可能性があります。" +
        "閉じてから、もう一度実行してください。";

    // ---- 監査記録（2030） ----

    private async Task RecordSafetyRejectionAsync(
        OldDatabaseDisposalRequest request,
        OldDatabaseTargetInspection inspection,
        string reason)
    {
        await RecordAuditAsync(request,
            $"以前の保存先のファイルの後始末: 拒否（対象の安全検査）。理由={reason}、" +
            $"方式={MethodName(request.Method)}、対象={FormatFiles(inspection.ExistingFiles)}").ConfigureAwait(false);
    }

    /// <summary>
    /// 削除の前ではない記録（終了・安全検査による拒否）。記録の失敗が処分の結果を覆い隠さないよう、例外を投げない
    /// 契約の呼び出し自体も保護する。
    /// </summary>
    private async Task RecordAuditAsync(OldDatabaseDisposalRequest request, string detail)
    {
        try
        {
            await _auditRecorder.RecordAsync(BuildAuditEvent(request, detail), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "後始末の監査記録に失敗しました（後始末の結果には影響しません）。");
        }
    }

    /// <summary>削除の前の記録。正本に載ったら <see langword="true"/>（載らなければ呼び出し側は先へ進まない）。</summary>
    private async Task<bool> RecordConfirmedAuditAsync(OldDatabaseDisposalRequest request, string detail)
    {
        try
        {
            if (await _auditRecorder.TryRecordConfirmedAsync(BuildAuditEvent(request, detail), CancellationToken.None)
                .ConfigureAwait(false))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "後始末の監査記録に失敗しました。");
        }

        _logger.LogWarning("後始末の削除の前の監査記録を書き込めないため、先へ進みません。");
        return false;
    }

    private AuditEvent BuildAuditEvent(OldDatabaseDisposalRequest request, string detail) => new(
        OccurredAt: _timeProvider.GetUtcNow(),
        Kind: AuditEventKind.OldDatabaseDisposalExecuted,
        RemoteAddress: request.OperatorAddress,
        RemotePort: null,
        Detail: detail,
        AuthenticationScheme: request.OperatorScheme,
        AuthenticatedPrincipal: request.OperatorPrincipal);

    private string BuildStartDetail(DisposalPlan plan)
    {
        var builder = new StringBuilder("以前の保存先のファイルの後始末: 開始。");
        builder.Append(CultureInfo.InvariantCulture, $"方式={MethodName(plan.Method)}、対象={FormatFiles(plan.Targets.ExistingFiles)}");
        builder.Append(CultureInfo.InvariantCulture, $"、対象の種類={plan.Kind}");
        if (plan.Destination is { } destination)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"、保管先の親フォルダ={destination.ParentDirectory}、作業用サブフォルダ={plan.WorkDirectory}、" +
                $"保管ファイル名={plan.ArchiveFileName}、保管先のドライブ種別={destination.Volume.DriveKind}" +
                $"（{destination.Volume.FileSystemName}）");
        }

        builder.Append("、移行の状態=").Append(FormatJudgment(plan.Judgment));
        builder.Append("、移行せずに後始末する=").Append(
            plan.Judgment is { RequiresConfirmation: true }
                ? (plan.Request.ConfirmDisposeWithoutMigration ? "確認あり" : "確認なし")
                : "確認不要");
        if (plan.Kind == OldDatabaseTargetKind.UnverifiableBody)
        {
            builder.Append("、内容を確認できないファイルの削除=").Append(plan.Request.ConfirmDeleteUnverifiableFile ? "確認あり" : "確認なし");
        }

        if (plan.Summary is { } summary && _environment.RetentionDays() is { } days)
        {
            builder.Append(CultureInfo.InvariantCulture, $"、ログ件数={summary.RecordCount}、保存期間={days} 日");
        }

        if (plan.RecordsBeyondRetention is { } beyond)
        {
            builder.Append(CultureInfo.InvariantCulture, $"、保存期間を過ぎたログ={beyond} 件（書き出したファイルに含む）");
        }

        return builder.ToString();
    }

    private static string BuildDeletionStartDetail(DisposalPlan plan, string finalPath, ArchiveExportResult export) =>
        "以前の保存先のファイルの後始末: 削除の開始。" +
        $"保管ファイル={finalPath}、SHA-256={export.Sha256}、整合性検査={export.IntegrityCheck}、" +
        $"件数照合=ログ {export.ExportedRecordCount}/{export.SourceRecordCount}・" +
        $"システムイベント {export.ExportedSystemEventCount}/{export.SourceSystemEventCount}、" +
        $"スキーマ版={export.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "不明"}、" +
        $"ReceivedAt={FormatTimestamp(export.OldestReceivedAt)}〜{FormatTimestamp(export.NewestReceivedAt)}、" +
        $"方式={MethodName(plan.Method)}";

    private static string BuildEndDetail(DisposalPlan plan, OldDatabaseDisposalResult result)
    {
        var builder = new StringBuilder("以前の保存先のファイルの後始末: 終了。結果=");
        builder.Append(result.Outcome switch
        {
            OldDatabaseDisposalOutcome.Completed => "完了",
            OldDatabaseDisposalOutcome.ArchiveFailed => "書き出しの失敗（元は変更なし）",
            OldDatabaseDisposalOutcome.DeletePartiallyFailed => "削除の部分失敗",
            OldDatabaseDisposalOutcome.Interrupted => "中断",
            _ => result.Outcome.ToString(),
        });
        builder.Append(CultureInfo.InvariantCulture, $"、方式={MethodName(plan.Method)}");
        if (result.Reason is { } reason)
        {
            builder.Append(CultureInfo.InvariantCulture, $"、理由={reason}（{result.Message}）");
        }

        if (result.ArchiveFilePath is { } archive)
        {
            builder.Append(CultureInfo.InvariantCulture, $"、保管ファイル={archive}、SHA-256={result.ArchiveSha256}");
        }

        if (result.RemainingFiles is { Count: > 0 } remaining)
        {
            builder.Append("、残ったファイル=").Append(string.Join("; ", remaining));
        }

        if (result.WorkDirectoryLeftBehind is { } leftBehind)
        {
            builder.Append("、残った作業用サブフォルダ=").Append(leftBehind);
        }

        if (plan.CleanedUpWorkDirectories.Count > 0)
        {
            builder.Append("、掃除した中断済みの作業用サブフォルダ=").Append(string.Join("; ", plan.CleanedUpWorkDirectories));
        }

        if (result.UncheckedArchiveParentDirectory is { } uncheckedParent)
        {
            builder.Append("、掃除を見送った保管先の親フォルダ=").Append(uncheckedParent).Append("（一覧できないため）");
        }

        if (plan.CheckpointSummary is { } checkpoint)
        {
            builder.Append("、移行チェックポイント=").Append(checkpoint);
        }

        return builder.ToString();
    }

    private static string FormatJudgment(OldDatabaseMigrationJudgment? judgment)
    {
        if (judgment is null)
        {
            return "判定なし（内容を読めない対象）";
        }

        if (judgment.Migrated)
        {
            return "移行済み";
        }

        return $"未完了（{string.Join("・", judgment.Gaps)}。未移行のログ {judgment.UnmigratedRecordCount} 件" +
               $"{FormatRange(judgment.UnmigratedOldest, judgment.UnmigratedNewest)}、" +
               $"未移行のシステムイベント {judgment.UnmigratedSystemEventCount} 件、" +
               $"切替の反映より後のログ {judgment.RecordsAfterSwitchCount} 件）";
    }

    private static string FormatFiles(IEnumerable<OldDatabaseFile> files) =>
        string.Join("; ", files.Select(file => $"{file.Path}({file.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "?"} バイト)"));

    private static string FormatRange(DateTimeOffset? oldest, DateTimeOffset? newest) =>
        oldest is null ? string.Empty : $"（{FormatTimestamp(oldest)}〜{FormatTimestamp(newest)}）";

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "-";

    private static string MethodName(OldDatabaseDisposalMethod method) =>
        method == OldDatabaseDisposalMethod.Archive ? "書き出して保管" : "削除";

    private static long SizeOf(OldDatabaseDisposalStatus status, string path) =>
        status.TargetFiles.FirstOrDefault(file => file.Path == path)?.SizeBytes ?? 0;

    private static OldDatabaseDisposalInspection EmptyInspection(
        OldDatabaseDisposalStatus status,
        OldDatabaseTargetKind kind,
        long requiredArchiveFreeBytes,
        string? rejectionReason = null,
        OldDatabaseDisposalReason? unavailableReason = null,
        string? confirmationDigest = null) =>
        new(status, kind, rejectionReason, ArchiveAllowed: false, null, null, null, null, null, null, null, requiredArchiveFreeBytes,
            unavailableReason, confirmationDigest);

    private static OldDatabaseDisposalResult Rejected(OldDatabaseDisposalReason reason, string message, OldDatabaseDisposalMethod method) =>
        new(OldDatabaseDisposalOutcome.Rejected, message, reason, method);

    private static string NewToken() => Guid.NewGuid().ToString("N");

    // ---- 内部型 ----

    private sealed record ContentRead(
        OldDatabaseTargetKind Kind,
        string? RejectionReason,
        OldDatabaseSummary? Summary,
        OldDatabaseMigrationJudgment? Judgment,
        OldDatabaseReadFailure? Failure = null);

    private sealed record DeletionOutcome(IReadOnlyList<string> Remaining, string? FailureMessage);

    private sealed record DisposalPlan(
        OldDatabaseDisposalMethod Method,
        OldDatabaseTargetKind Kind,
        OldDatabaseTargetInspection Targets,
        OldDatabaseSummary? Summary,
        OldDatabaseMigrationJudgment? Judgment,
        OldDatabaseDisposalRequest Request,
        ArchiveDestination? Destination,
        long? DataRootFloorBytes,
        string? WorkDirectory,
        string? ArchiveFileName,
        long? RecordsBeyondRetention)
    {
        public IReadOnlyList<string> CleanedUpWorkDirectories { get; set; } = [];

        /// <summary>親フォルダを一覧できず、クラッシュ後の掃除を見送ったか。</summary>
        public bool CleanupSkipped { get; set; }

        /// <summary>作業用サブフォルダの保持（書き出して保管のみ。処分の終了で手放す）。</summary>
        public IArchiveWorkDirectoryHold? WorkDirectoryHold { get; init; }

        /// <summary>書き出したファイルを確定した（改名と最終の ACL）か。確定後は作業用サブフォルダを片付けない。</summary>
        public bool Finalized { get; set; }

        public string? CheckpointSummary { get; set; }
    }

    /// <summary>実行中の処分の段階と取り消し（段階・取り消し可否は状態の読み取りと並行して更新される）。</summary>
    private sealed class RunState : IDisposable
    {
        private readonly CancellationTokenSource _userCancellation = new();
        private readonly CancellationTokenSource _linked;
        private volatile bool _cancellable = true;
        private int _stage = (int)OldDatabaseDisposalStage.Checkpointing;
        private int _abortReason = -1;

        public RunState(string idempotencyToken, OldDatabaseDisposalMethod method, DateTimeOffset startedAt, CancellationToken hostStopping)
        {
            IdempotencyToken = idempotencyToken;
            Method = method;
            StartedAt = startedAt;
            HostStoppingToken = hostStopping;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_userCancellation.Token, hostStopping);
        }

        public string IdempotencyToken { get; }

        public OldDatabaseDisposalMethod Method { get; }

        public DateTimeOffset StartedAt { get; }

        public CancellationToken HostStoppingToken { get; }

        /// <summary>利用者の取り消し・停止要求のどちらかで取り消される。</summary>
        public CancellationToken Token => _linked.Token;

        public bool StoppedByHost => HostStoppingToken.IsCancellationRequested;

        public bool Cancellable => _cancellable;

        public OldDatabaseDisposalStage Stage => (OldDatabaseDisposalStage)Volatile.Read(ref _stage);

        public OldDatabaseDisposalReason? AbortReason
        {
            get
            {
                var value = Volatile.Read(ref _abortReason);
                return value < 0 ? null : (OldDatabaseDisposalReason)value;
            }
            set => Volatile.Write(ref _abortReason, value is null ? -1 : (int)value.Value);
        }

        public void SetStage(OldDatabaseDisposalStage stage) => Volatile.Write(ref _stage, (int)stage);

        public void MakeNonCancellable() => _cancellable = false;

        public void CancelByUser()
        {
            if (_cancellable)
            {
                _userCancellation.Cancel();
            }
        }

        public void Dispose()
        {
            _linked.Dispose();
            _userCancellation.Dispose();
        }
    }
}
