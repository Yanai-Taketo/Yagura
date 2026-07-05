namespace Yagura.Abstractions.Auditing;

/// <summary>
/// 監査記録 1 件分の内容（security.md §4.1「記録内容」）。
/// </summary>
/// <param name="OccurredAt">事象発生時刻（UTC）。</param>
/// <param name="Kind">事象種別。</param>
/// <param name="RemoteAddress">接続元アドレス（<c>HttpContext.Connection.RemoteIpAddress</c> 由来。
/// 認証 opt-in が無効の間の「誰が」に相当する——security.md §4.1）。</param>
/// <param name="RemotePort">接続元ポート（<c>HttpContext.Connection.RemotePort</c> 由来）。</param>
/// <param name="AttemptedPath">試行されたパス（拒否系事象で使用。要求ボディは含めない。
/// 管理操作（2000 番台）では対象パスの概念がないため <see langword="null"/> でよい）。</param>
/// <param name="ReachedListenerPort">到達したリスナの実ポート番号（拒否系事象で使用）。</param>
/// <param name="Detail">
/// 事象の要約（security.md §4.1「何を・変更前後の要約」。M8-4 で追加）。
/// <b>秘密情報（パスワード・接続文字列の資格情報部・提示 SQL の原文）を含めてはならない</b>
/// （configuration.md §2・§5・database.md §5.2 の各規則）。設定変更では変更キーと前後値
/// （秘密情報キーは値をマスク）を、昇格では成否・選択内容を記す。
/// </param>
/// <remarks>
/// <b>秘密情報・要求ボディは記録しない</b>（security.md §4.1・§4.2 の共通制約）。本レコードの
/// フィールドはいずれも「誰が・いつ・何を試みたか / 実行したか」の最小限であり、クエリ文字列・
/// ヘッダ・ボディ等の要求内容そのものは持たない。
/// M8-4 で <c>Yagura.Storage.Auditing</c> から移設した（<see cref="AuditEventKind"/> の remarks 参照）。
/// </remarks>
public sealed record AuditEvent(
    DateTimeOffset OccurredAt,
    AuditEventKind Kind,
    string? RemoteAddress,
    int? RemotePort,
    string? AttemptedPath = null,
    int? ReachedListenerPort = null,
    string? Detail = null);
