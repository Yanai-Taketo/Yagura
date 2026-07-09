namespace Yagura.Storage;

/// <summary>
/// 1 件の syslog ログレコード（論理スキーマ。database.md §2.1）。
/// </summary>
/// <param name="ReceivedAt">サーバが受信した時刻（UTC）。検索・並び・保持期間の基準軸。</param>
/// <param name="SourceAddress">送信元アドレス。</param>
/// <param name="SourcePort">送信元ポート。</param>
/// <param name="Protocol">受信リスナのトランスポートプロトコル。</param>
/// <param name="ParseStatus">解析結果（排他 3 値）。</param>
/// <param name="Id">
/// レコード識別子。provider が採番するため、挿入前のレコードでは <c>null</c> を許容する。
/// </param>
/// <param name="DeviceTimestamp">
/// 送信元が名乗った時刻（あれば）。参考情報であり基準軸にしない（database.md §2.2）。
/// 解析できた範囲で設定する——<see cref="ParseStatus"/> が解析失敗のレコードにも及ぶ
/// （database.md §2.1「解析失敗時のフィールド保持」）。
/// </param>
/// <param name="Facility">
/// syslog PRI の facility 分解値。PRI 自体が解析できなかった場合は未設定。PRI 確定後の
/// より後段の解析失敗（非 UTF-8 本文等）では、解析失敗のレコードでも保持される
/// （database.md §2.1「解析失敗時のフィールド保持」）。
/// </param>
/// <param name="Severity">
/// syslog PRI の severity 分解値。未設定・保持の条件は <paramref name="Facility"/> と同じ。
/// </param>
/// <param name="Hostname">
/// RFC 5424 HOSTNAME（RFC 3164 は対応部分をマップ）。解析できた範囲で設定する——
/// 解析失敗のレコードにも及ぶ（database.md §2.1「解析失敗時のフィールド保持」）。
/// </param>
/// <param name="AppName">RFC 5424 APP-NAME。設定条件は <paramref name="Hostname"/> と同じ。</param>
/// <param name="ProcId">RFC 5424 PROCID。設定条件は <paramref name="Hostname"/> と同じ。</param>
/// <param name="MsgId">RFC 5424 MSGID。設定条件は <paramref name="Hostname"/> と同じ。</param>
/// <param name="StructuredData">
/// RFC 5424 構造化データ。原文のまま保存（要素分解はしない）。解析できた範囲で設定する——
/// 解析失敗のレコードにも及ぶが、自身が非 UTF-8 の場合は未設定（Raw に生バイト列が残る）。
/// </param>
/// <param name="Message">メッセージ本文。非 UTF-8 で解析失敗した場合は未設定（Raw に生バイト列が残る）。</param>
/// <param name="Raw">
/// 受信したバイト列そのもの。<see cref="ParseStatus"/> が解析失敗・不完全のときのみ非 null
/// （database.md §2.1。解析済みレコードには保存しない）。
/// </param>
public sealed record LogRecord(
    DateTimeOffset ReceivedAt,
    string SourceAddress,
    int SourcePort,
    Protocol Protocol,
    ParseStatus ParseStatus,
    long? Id = null,
    DateTimeOffset? DeviceTimestamp = null,
    int? Facility = null,
    int? Severity = null,
    string? Hostname = null,
    string? AppName = null,
    string? ProcId = null,
    string? MsgId = null,
    string? StructuredData = null,
    string? Message = null,
    byte[]? Raw = null);
