namespace Yagura.Abstractions.Auditing;

/// <summary>
/// 監査記録の書き込み口（security.md §4）。
/// </summary>
/// <remarks>
/// <para>
/// <b>配置（M8-4 で <c>Yagura.Storage.Auditing</c> から移設）</b>: 本インターフェースは
/// <c>Yagura.Web</c>（リスナガード・circuit 管理）と <c>Yagura.Host</c>（ウィザードサービス・
/// 実体の結線）の両方が使うモジュール横断契約であり、監査記録の実体はログ本体の永続化
/// （provider 抽象 = Storage の管轄）とは独立した「ホスト管轄のローカルファイル + Windows
/// イベントログ併記」である（security.md §4.2）。architecture.md §1.1 の M6-4 申し送り
/// 「<c>IAuditRecorder</c> 等、現在 <c>Yagura.Storage</c> にある横断契約の移設も M8 で判断する」を
/// 「移設する」で決着させた。実体（<c>Yagura.Host.Observability.Auditing.FileAuditRecorder</c>）は
/// 引き続き <c>Yagura.Host</c> が DI で結線する。
/// </para>
/// <para>
/// <b>失敗しても要求処理を妨げない契約</b>（ADR-0004 決定 7・security.md §4.2）:
/// <see cref="RecordAsync"/> は例外を投げない契約とする。実装内部でファイル書き込み・イベント
/// ログ書き込みの両方が失敗しても、呼び出し元（<c>ListenerPortGuardMiddleware</c>・ウィザード
/// サービス等）の要求処理を妨げてはならない。実装は失敗をカウンタで観測可能にする
/// （<c>Yagura.Web.Diagnostics.WebGuardMetrics.RecordAuditWriteFailed</c>。
/// architecture.md §4.1.1 の計器一覧参照）。
/// </para>
/// </remarks>
public interface IAuditRecorder
{
    /// <summary>
    /// 監査事象を 1 件記録する。失敗しても例外を投げない（本インターフェースの remarks 参照）。
    /// </summary>
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
