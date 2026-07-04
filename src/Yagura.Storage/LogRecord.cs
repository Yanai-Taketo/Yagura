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
/// <param name="DeviceTimestamp">送信元が名乗った時刻（あれば）。参考情報であり基準軸にしない（database.md §2.2）。</param>
/// <param name="Facility">syslog PRI の facility 分解値。解析失敗時は未設定。</param>
/// <param name="Severity">syslog PRI の severity 分解値。解析失敗時は未設定。</param>
/// <param name="Hostname">RFC 5424 HOSTNAME（RFC 3164 は対応部分をマップ）。</param>
/// <param name="AppName">RFC 5424 APP-NAME。</param>
/// <param name="ProcId">RFC 5424 PROCID。</param>
/// <param name="MsgId">RFC 5424 MSGID。</param>
/// <param name="StructuredData">RFC 5424 構造化データ。原文のまま保存（要素分解はしない）。</param>
/// <param name="Message">メッセージ本文。</param>
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
