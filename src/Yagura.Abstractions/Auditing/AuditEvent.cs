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
/// <param name="AuthenticationScheme">
/// 認証に使われた方式（ADR-0010 決定 3・6。<c>"windows"</c> / <c>"app"</c>。認証 opt-in が
/// 無効、または loopback 認証 opt-in 無効時の loopback 経由操作では <see langword="null"/>——
/// この場合「誰が」欄は <see cref="RemoteAddress"/> のみが実効値になる（security.md §4.1
/// の射程限定のとおり）。
/// </param>
/// <param name="AuthenticatedPrincipal">
/// 認証済み利用者名（ADR-0010 決定 3・6 の「命名空間つき表記」の名前部分。Windows 統合認証は
/// <c>DOMAIN\user</c> 形式、アプリ独自認証は登録ユーザー名。<see cref="AuthenticationScheme"/>
/// と組み合わせて初めて一意な表記になる——例: <c>windows:CONTOSO\jdoe</c> /
/// <c>app:admin1</c>（実際の連結は呼び出し側・表示側で行う。本フィールドは名前部分のみ保持する）。
/// </param>
/// <remarks>
/// <b>秘密情報・要求ボディは記録しない</b>（security.md §4.1・§4.2 の共通制約）。本レコードの
/// フィールドはいずれも「誰が・いつ・何を試みたか / 実行したか」の最小限であり、クエリ文字列・
/// ヘッダ・ボディ等の要求内容そのものは持たない。
/// M8-4 で <c>Yagura.Storage.Auditing</c> から移設した（<see cref="AuditEventKind"/> の remarks 参照）。
/// <see cref="AuthenticationScheme"/>・<see cref="AuthenticatedPrincipal"/> は ADR-0010（Phase 1）で
/// additive に追加した——既存呼び出し元は省略時 <see langword="null"/> のまま変更不要。
/// </remarks>
public sealed record AuditEvent(
    DateTimeOffset OccurredAt,
    AuditEventKind Kind,
    string? RemoteAddress,
    int? RemotePort,
    string? AttemptedPath = null,
    int? ReachedListenerPort = null,
    string? Detail = null,
    string? AuthenticationScheme = null,
    string? AuthenticatedPrincipal = null);
