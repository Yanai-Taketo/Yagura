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
/// <b>スコープ外（Issue #52 に明記済み）</b>: 記録失敗中の事象のメモリ内保持・チャネル復旧後の
/// 書き戻し（SEC-10）は本クラスの責務に含めない。
/// </para>
/// </remarks>
public sealed class FileAuditRecorder : IAuditRecorder
{
    /// <summary>データルート配下の監査記録ディレクトリ名。</summary>
    public const string DirectoryName = "audit";

    /// <summary>監査記録ファイル名。</summary>
    public const string FileName = "audit.jsonl";

    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly WebGuardMetrics _metrics;
    private readonly object _writeGate = new();

    public FileAuditRecorder(string dataRoot, ILogger logger, WebGuardMetrics metrics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);

        _filePath = Path.Combine(dataRoot, DirectoryName, FileName);
        _logger = logger;
        _metrics = metrics;
    }

    /// <inheritdoc/>
    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var eventId = ResolveEventId(auditEvent.Kind);

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

        return Task.CompletedTask;
    }

    private bool TryAppendToFile(AuditEvent auditEvent, EventId eventId)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

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
                    _filePath,
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
                _filePath);
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

    private static EventId ResolveEventId(AuditEventKind kind) => kind switch
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
        AuditEventKind.AdminLoginSucceeded => AuditEventIds.AdminLoginSucceeded,
        AuditEventKind.AdminAuthorizationDenied => AuditEventIds.AdminAuthorizationDenied,
        AuditEventKind.AdminHttpsCertificatePrivateKeyAccessGranted => AuditEventIds.AdminHttpsCertificatePrivateKeyAccessGranted,
        AuditEventKind.IngestionTlsCertificatePrivateKeyAccessGranted => AuditEventIds.IngestionTlsCertificatePrivateKeyAccessGranted,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知の監査事象種別。"),
    };

    /// <summary>
    /// イベントレベルの区画割当（security.md §4.3——2000 番台 = 管理操作 = 情報 /
    /// 3000 番台 = 拒否・セキュリティ事象 = 警告。「レベルだけで最低限の監視が組める」の実装）。
    /// </summary>
    internal static LogLevel ResolveLogLevel(EventId eventId) =>
        eventId.Id is >= 2000 and < 3000 ? LogLevel.Information : LogLevel.Warning;
}
