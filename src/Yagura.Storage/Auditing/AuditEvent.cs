namespace Yagura.Storage.Auditing;

/// <summary>
/// 監査記録 1 件分の内容（security.md §4.1「記録内容」）。
/// </summary>
/// <param name="OccurredAt">事象発生時刻（UTC）。</param>
/// <param name="Kind">事象種別。</param>
/// <param name="RemoteAddress">接続元アドレス（<c>HttpContext.Connection.RemoteIpAddress</c> 由来）。</param>
/// <param name="RemotePort">接続元ポート（<c>HttpContext.Connection.RemotePort</c> 由来）。</param>
/// <param name="AttemptedPath">試行されたパス（要求ボディは含めない。秘密情報を記録しないという
/// security.md §4.1 の制約に従う）。</param>
/// <param name="ReachedListenerPort">到達したリスナの実ポート番号。</param>
/// <remarks>
/// <b>秘密情報・要求ボディは記録しない</b>（security.md §4.1・§4.2 の共通制約）。本レコードの
/// フィールドはいずれも「誰が・いつ・何を試みたか」の最小限であり、クエリ文字列・ヘッダ・
/// ボディ等の要求内容そのものは持たない。
/// </remarks>
public sealed record AuditEvent(
    DateTimeOffset OccurredAt,
    AuditEventKind Kind,
    string? RemoteAddress,
    int? RemotePort,
    string AttemptedPath,
    int ReachedListenerPort);
