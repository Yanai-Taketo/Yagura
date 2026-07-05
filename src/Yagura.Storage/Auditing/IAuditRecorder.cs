namespace Yagura.Storage.Auditing;

/// <summary>
/// 監査記録の書き込み口（security.md §4）。
/// </summary>
/// <remarks>
/// <para>
/// <b>設計意図</b>: architecture.md の参照構造（Web 層は Storage の抽象のみを参照し、具体的な
/// ファイル I/O・Windows イベントログ書き込みは Host 側が結線する）に合わせ、本インターフェースは
/// <c>Yagura.Storage</c> に置く。<c>Yagura.Web</c>（閲覧・管理リスナのミドルウェア等）は本
/// インターフェースのみを参照し、実体（ローカルファイル + Windows イベントログの併記）は
/// <c>Yagura.Host</c> が DI で結線する（<c>Yagura.Host.Observability.Auditing.FileAuditRecorder</c>
/// 参照）。
/// </para>
/// <para>
/// <b>失敗しても要求処理を妨げない契約</b>（ADR-0004 決定 7・security.md §4.2）:
/// <see cref="RecordAsync"/> は例外を投げない契約とする。実装内部でファイル書き込み・イベント
/// ログ書き込みの両方が失敗しても、呼び出し元（<c>ListenerPortGuardMiddleware</c> 等）の
/// 要求処理（404 応答等）を妨げてはならない。実装は失敗をカウンタで観測可能にする
/// （<c>Yagura.Ingestion.Diagnostics.IngestionMetrics.RecordAuditWriteFailed</c> 等、
/// 呼び出し側の計器体系に委ねる）。
/// </para>
/// </remarks>
public interface IAuditRecorder
{
    /// <summary>
    /// 監査事象を 1 件記録する。失敗しても例外を投げない（本インターフェースの remarks 参照）。
    /// </summary>
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
