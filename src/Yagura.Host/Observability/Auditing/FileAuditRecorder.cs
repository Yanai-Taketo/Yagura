using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Diagnostics;

namespace Yagura.Host.Observability.Auditing;

/// <summary>
/// <see cref="IAuditRecorder"/> の実体（security.md §4.1・§4.2。M6-2。Issue #52）。
/// アプリ記録の実体はホスト管轄の専用ローカルファイル（追記型 JSON Lines）とし、
/// Windows イベントログへ併記する。
/// </summary>
/// <remarks>
/// <para>
/// <b>出力先ディレクトリ</b>: データルート配下の <c>audit</c> サブディレクトリ
/// （<see cref="DirectoryName"/>）。<c>spool</c>・メタデータ領域と同様、将来の ACL 分離
/// （SEC-3。サービスアカウントへ追記系のみを許す構成の検討）を見込んだ専用領域とする
/// （security.md §5「監査記録の領域は例外として分離を検討する」）。
/// </para>
/// <para>
/// <b>ファイル形式</b>: 追記型の JSON Lines（1 事象 1 行）。security.md §4.2 が「機械可読・
/// 部分破損に強い」という理由で追記型を推奨しており、メタデータ領域
/// （<see cref="MetadataStore"/>。全体書き換え + 原子的置換）とは異なる判断になる——
/// 監査記録は低頻度の全体書き換えに適した「状態」ではなく、高頻度に増え続ける「事象の列」
/// であり、スプール（<see cref="Yagura.Storage.Spool.DiskSpool"/>）と同じ追記の性質を持つため。
/// スプールの独自バイナリフレーム形式ではなく JSON Lines を選んだ理由は、監査記録が
/// 人間による事後調査・外部ツールでの解析（grep・jq 等）の対象になることを優先したため
/// （書き込み頻度がスプールより遥かに低く、パース性能上の制約が緩い）。
/// </para>
/// <para>
/// <b>日次ローテーション（SEC-2/SEC-3。Issue #261）</b>: 追記先は事象発生日（UTC）ごとの
/// ファイル <c>audit-yyyyMMdd.jsonl</c>（<see cref="GetFileNameFor"/>）。単一ファイル
/// （<see cref="LegacyFileName"/>）への無制限追記をやめ、日付でファイルを分割することで、
/// ①保持期間超過分の削除が「期限切れファイルの削除」だけで済む（既存内容の書き換え・切り詰めが
/// 一切不要——SEC-3 の追記専用 ACE 構成と両立する。サイズローテーションの rename 方式は
/// 既存ファイルへの DELETE 権限を要するため採らない）②削除の単位が日単位になり、保持期間
/// （日数指定。SEC-2）の意味と一致する。ローテーション自体はファイル名の切り替えのみであり
/// 事象として記録しない（削除は <see cref="AuditRetentionScheduler"/> が 2015 として記録する）。
/// 旧来の単一ファイルが残る環境では、そのファイルは追記されなくなり、最終書き込みから保持期間が
/// 経過した時点で削除対象になる（<see cref="AuditRetentionScheduler"/> の削除判定参照）。
/// </para>
/// <para>
/// <b>多段の失敗処理（security.md §4.2 の最小実装）</b>: (1) アプリ記録ファイルへの追記を試みる。
/// (2) 成功・失敗いずれの場合も Windows イベントログへ 3000 番台の警告として書き出す
/// （<see cref="ILogger"/> → <c>EventLog</c> プロバイダの既存配線。<c>EventId</c> を明示指定する）。
/// (3) ファイル追記・イベントログの両方が失敗した場合は <see cref="WebGuardMetrics.RecordAuditWriteFailed"/>
/// でカウンタに計上する（黙って握りつぶさない。§4.2「それも失敗したらゲージ・状態画面」の
/// 最小実装として、まずカウンタで観測可能にする——ゲージ・状態画面の実装は M8 以降）。
/// **いずれの段が失敗しても、本メソッドは例外を投げない**（ADR-0004 決定 7「監査記録の
/// 書き込み不能は要求処理を妨げない」）。
/// </para>
/// <para>
/// <b>スコープの分界（Issue #52・#269）</b>: 記録失敗中の事象のメモリ内保持・チャネル復旧後の
/// 書き戻し（SEC-10）は本クラスの責務に含めない——本クラスは「1 事象を 2 チャネルへ 1 回書く」
/// 単責務に留め、失敗中の保持・書き戻しは <see cref="ResilientAuditRecorder"/>（デコレータ）が
/// 担う。そのために本クラスは書き込みの成否を <see cref="TryRecord"/> で呼び出し元へ返す
/// （<see cref="RecordAsync"/> は <see cref="IAuditRecorder"/> 契約どおり成否を隠して例外も投げない）。
/// </para>
/// </remarks>
public sealed class FileAuditRecorder : IAuditRecorder
{
    /// <summary>データルート配下の監査記録ディレクトリ名。</summary>
    public const string DirectoryName = "audit";

    /// <summary>
    /// 日次ローテーション導入（Issue #261）前の単一ファイル名。新規の追記先には使わない
    /// （既存環境に残るファイルの識別・削除判定用に保持する。クラス remarks 参照）。
    /// </summary>
    public const string LegacyFileName = "audit.jsonl";

    /// <summary>
    /// 監査記録ファイルの列挙パターン（日次ファイル <c>audit-yyyyMMdd.jsonl</c> と
    /// 旧単一ファイル <c>audit.jsonl</c> の両方に一致する。<see cref="AuditRetentionScheduler"/>
    /// の削除対象の列挙に使う）。
    /// </summary>
    public const string AuditFileSearchPattern = "audit*.jsonl";

    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly string _directoryPath;
    private readonly ILogger _logger;
    private readonly WebGuardMetrics _metrics;
    private readonly object _writeGate = new();

    public FileAuditRecorder(string dataRoot, ILogger logger, WebGuardMetrics metrics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);

        _directoryPath = Path.Combine(dataRoot, DirectoryName);
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// 事象発生日（UTC）に対応する日次監査記録ファイル名を返す（<c>audit-yyyyMMdd.jsonl</c>）。
    /// 日付の基準は事象自体の <see cref="AuditEvent.OccurredAt"/>（書き込み時点の時計ではない）——
    /// 遅延書き込みでも事象は発生日のファイルへ入り、ファイル名と内容の日付が一致する。
    /// </summary>
    public static string GetFileNameFor(DateTimeOffset occurredAt) =>
        $"audit-{occurredAt.UtcDateTime:yyyyMMdd}.jsonl";

    /// <summary>
    /// <see cref="TryRecord"/> の結果——アプリ記録ファイル・イベントログそれぞれの書き込み成否。
    /// SEC-10 のデコレータ（<see cref="ResilientAuditRecorder"/>）が「アプリ記録ファイルへ確実に
    /// 残ったか」を判定し、失敗中の保持・復旧後の書き戻しを制御するために用いる。
    /// </summary>
    internal readonly record struct AuditWriteOutcome(bool FileSucceeded, bool EventLogSucceeded);

    /// <inheritdoc/>
    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        TryRecord(auditEvent);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 1 事象をアプリ記録ファイル + イベントログの 2 チャネルへ書き、それぞれの成否を返す
    /// （<see cref="RecordAsync"/> と同じ多段の失敗処理。ただし成否を隠さず呼び出し元へ返す点が
    /// 異なる）。<see cref="RecordAsync"/> 同様、いずれの段が失敗しても例外は投げない
    /// （ADR-0004 決定 7）。SEC-10 のデコレータが書き戻し判定に使う。
    /// </summary>
    internal AuditWriteOutcome TryRecord(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        EventId eventId;
        try
        {
            eventId = ResolveEventId(auditEvent.Kind);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // never-throw 契約（ADR-0004 決定 7）の最終防衛線。EventId 解決表への追随漏れ
            // （enum に事象種別を足したが switch を更新し忘れた等）があっても、監査記録・要求処理を
            // 止めてはならない——特に認証拒否の記録経路で throw すると、攻撃を受けている最中に
            // 要求が 500 になり、かつ攻撃の証跡が沈黙する。未知 Kind は EventId 0（種別名を Name に
            // 残す）で最善努力で記録し、ドリフト自体は AuditEventKind 全網羅テストが機械検出する。
            Console.Error.WriteLine($"[audit-eventid-unresolved] {ex.Message}");
            eventId = new EventId(0, auditEvent.Kind.ToString());
        }

        var fileWriteSucceeded = TryAppendToFile(auditEvent, eventId);

        // Windows イベントログへの併記は、アプリ記録ファイルの成否に関わらず常に試みる
        // （security.md §4.2「アプリ記録が書けなければイベントログへ」——アプリ記録が
        // 書けた場合も、独立した経路として二重に記録すること自体が改変耐性の下支えになる。
        // ADR-0004 決定 7。レベルは §4.3 の区画割当のとおり——2000 番台 = 情報 /
        // 3000 番台 = 警告——を ResolveLogLevel で機械的に適用する）。
        var eventLogWriteSucceeded = TryWriteEventLog(auditEvent, eventId);

        if (!fileWriteSucceeded && !eventLogWriteSucceeded)
        {
            // 両方の経路が失敗した場合のみカウンタへ計上する（security.md §4.2 の多段の
            // 最終段。「黙って握りつぶさずカウンタで観測可能にする」）。
            _metrics.RecordAuditWriteFailed();
        }

        return new AuditWriteOutcome(fileWriteSucceeded, eventLogWriteSucceeded);
    }

    private bool TryAppendToFile(AuditEvent auditEvent, EventId eventId)
    {
        var filePath = Path.Combine(_directoryPath, GetFileNameFor(auditEvent.OccurredAt));

        try
        {
            Directory.CreateDirectory(_directoryPath);

            var line = new AuditFileLine
            {
                OccurredAtUtc = auditEvent.OccurredAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Kind = auditEvent.Kind.ToString(),
                EventId = eventId.Id,
                RemoteAddress = auditEvent.RemoteAddress,
                RemotePort = auditEvent.RemotePort,
                AttemptedPath = auditEvent.AttemptedPath,
                ReachedListenerPort = auditEvent.ReachedListenerPort,
                Detail = auditEvent.Detail,
                AuthenticationScheme = auditEvent.AuthenticationScheme,
                AuthenticatedPrincipal = auditEvent.AuthenticatedPrincipal,
            };

            var json = JsonSerializer.Serialize(line, SerializerOptions);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");

            // 追記型 I/O。1 プロセス内の並行呼び出しはロックで直列化する（スプールと同様、
            // 監査事象の発生頻度は低く単純なロックで正しさを優先してよい）。プロセス間の
            // 同時書き込み（想定しない構成）に対する排他は行わない。
            lock (_writeGate)
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ファイル I/O 障害（ディスク満杯・ACL 不整合等）。要求処理は妨げない
            // （ADR-0004 決定 7）——ここでは失敗した事実だけを呼び出し元へ返す。
            _logger.LogWarning(
                ex,
                "[audit-file-write-failed] 監査記録ファイル {Path} への書き込みに失敗しました。",
                filePath);
            return false;
        }
    }

    private bool TryWriteEventLog(AuditEvent auditEvent, EventId eventId)
    {
        try
        {
            // イベントログ本文は日本語の説明（AuditEventDescriptions）を使う——{Kind} を
            // そのまま埋め込むと英語 enum 名（ViewerListenerAdminRequestRejected 等）が
            // 運用者向けの文面に漏れるため（2026-07-06 イベントログ日本語化）。
            // アプリ記録ファイル側（AuditFileLine.Kind）は機械可読性を優先し enum 名のまま
            // 維持するため、本メソッドの変更はイベントログ本文にのみ影響する。
            // 「誰が」欄（ADR-0010 決定 3・6）: 認証済みなら方式つき利用者名、未認証（または
            // 認証 opt-in 無効）なら接続元のみ——security.md §4.1 の記録内容の実装。
            var who = auditEvent.AuthenticatedPrincipal is { Length: > 0 }
                ? $"{auditEvent.AuthenticationScheme}:{auditEvent.AuthenticatedPrincipal} ({auditEvent.RemoteAddress ?? "(unknown)"}:{auditEvent.RemotePort})"
                : $"{auditEvent.RemoteAddress ?? "(unknown)"}:{auditEvent.RemotePort}";

            _logger.Log(
                ResolveLogLevel(eventId),
                eventId,
                "[audit] {Description}: 実行者={Who} 試行パス={AttemptedPath} 到達リスナポート={ReachedListenerPort} 要約={Detail}",
                AuditEventDescriptions.Describe(auditEvent.Kind),
                who,
                auditEvent.AttemptedPath,
                auditEvent.ReachedListenerPort,
                auditEvent.Detail);
            return true;
        }
        catch (Exception ex)
        {
            // 本リポジトリの他の I/O 境界（MetadataStore・DiskSpool 等）は catch (Exception ex)
            // when (ex is 具体型…) という限定捕捉を規約とするが、本メソッドはあえて無条件の
            // catch (Exception) にする——ILogger 実装（EventLog プロバイダ含む）が投げ得る
            // 例外の型は呼び出し側から見て閉じた集合ではなく（プロバイダ実装は差し替え可能。
            // Program.cs のコメントのとおり EventLog プロバイダ自体は内部で例外を握りつぶす
            // 実装だが、その保証は本クラスの契約ではなく実装詳細である）、本メソッドは
            // 「監査記録・イベントログ併記の多段のうち最後の砦」（ADR-0004 決定 7「監査記録の
            // 書き込み不能は要求処理自体を妨げない」を本メソッドの外側でも維持する最終防衛線）
            // であるため、想定外の例外型であっても呼び出し元（ListenerPortGuardMiddleware）へ
            // 伝播させてはならない。
            Console.Error.WriteLine($"[audit-eventlog-write-failed] {ex.Message}");
            return false;
        }
    }

    // internal: AuditEventKind の全値がここで固有 EventId へ解決されることを
    // ResolveEventId_maps_every_AuditEventKind テストが機械検証する（switch 追随漏れの検出）。
    internal static EventId ResolveEventId(AuditEventKind kind) => kind switch
    {
        AuditEventKind.ViewerListenerAdminRequestRejected => AuditEventIds.ViewerListenerAdminRequestRejected,
        AuditEventKind.ConfigurationSaved => AuditEventIds.ConfigurationSaved,
        AuditEventKind.PromotionConnectionValidated => AuditEventIds.PromotionConnectionValidated,
        AuditEventKind.PromotionExecuted => AuditEventIds.PromotionExecuted,
        AuditEventKind.CircuitDisconnected => AuditEventIds.CircuitDisconnected,
        AuditEventKind.CircuitOriginRejected => AuditEventIds.CircuitOriginRejected,
        AuditEventKind.ForwarderKitGenerated => AuditEventIds.ForwarderKitGenerated,
        AuditEventKind.AdminAuthenticationConfigured => AuditEventIds.AdminAuthenticationConfigured,
        AuditEventKind.AdminAccountCreated => AuditEventIds.AdminAccountCreated,
        AuditEventKind.WindowsAuthenticationHandshakeFailed => AuditEventIds.WindowsAuthenticationHandshakeFailed,
        AuditEventKind.AppAuthenticationLoginFailed => AuditEventIds.AppAuthenticationLoginFailed,
        AuditEventKind.AdminAccountLockedOut => AuditEventIds.AdminAccountLockedOut,
        // ADR-0011 三層防御の拒否事象（3005 ロックアウトの後継。#232 で enum・EventId・emit を
        // 追加した際に本解決表への追加が漏れていた——認証拒否記録経路での throw を招く）。
        AuditEventKind.AdminAuthBackoffCapReached => AuditEventIds.AdminAuthBackoffCapReached,
        AuditEventKind.AdminAuthRateLimited => AuditEventIds.AdminAuthRateLimited,
        AuditEventKind.AdminLoginSucceeded => AuditEventIds.AdminLoginSucceeded,
        AuditEventKind.AdminAuthorizationDenied => AuditEventIds.AdminAuthorizationDenied,
        AuditEventKind.AdminHttpsCertificatePrivateKeyAccessGranted => AuditEventIds.AdminHttpsCertificatePrivateKeyAccessGranted,
        AuditEventKind.IngestionTlsCertificatePrivateKeyAccessGranted => AuditEventIds.IngestionTlsCertificatePrivateKeyAccessGranted,
        AuditEventKind.AdminRemoteBindingConfigured => AuditEventIds.AdminRemoteBindingConfigured,
        AuditEventKind.AdminHttpsCertificateConfigured => AuditEventIds.AdminHttpsCertificateConfigured,
        AuditEventKind.AdminSessionsInvalidated => AuditEventIds.AdminSessionsInvalidated,
        AuditEventKind.ViewerLoginSucceeded => AuditEventIds.ViewerLoginSucceeded,
        AuditEventKind.ViewerAuthorizationDenied => AuditEventIds.ViewerAuthorizationDenied,
        AuditEventKind.AuditRetentionApplied => AuditEventIds.AuditRetentionApplied,
        AuditEventKind.ConfigurationReloaded => AuditEventIds.ConfigurationReloaded,
        AuditEventKind.InstallationRecordTranscribed => AuditEventIds.InstallationRecordTranscribed,
        AuditEventKind.LogMigrationExecuted => AuditEventIds.LogMigrationExecuted,
        AuditEventKind.CircuitRevocationGraceGranted => AuditEventIds.CircuitRevocationGraceGranted,
        AuditEventKind.CircuitRevocationGraceEnded => AuditEventIds.CircuitRevocationGraceEnded,
        AuditEventKind.RejectionAggregated => AuditEventIds.RejectionAggregated,
        AuditEventKind.AuditChannelRecovered => AuditEventIds.AuditChannelRecovered,
        AuditEventKind.StartupConfigurationChangeDetected => AuditEventIds.StartupConfigurationChangeDetected,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知の監査事象種別。"),
    };

    /// <summary>
    /// イベントレベルの区画割当（security.md §4.3——2000 番台 = 管理操作 = 情報 /
    /// 3000 番台 = 拒否・セキュリティ事象 = 警告。「レベルだけで最低限の監視が組める」の実装）。
    /// </summary>
    internal static LogLevel ResolveLogLevel(EventId eventId) =>
        eventId.Id is >= 2000 and < 3000 ? LogLevel.Information : LogLevel.Warning;
}
