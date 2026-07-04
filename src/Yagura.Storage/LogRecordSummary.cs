namespace Yagura.Storage;

/// <summary>
/// 一覧表示用の軽量射影（database.md §1.2「対話的検索」契約）。
/// <see cref="LogRecord.Raw"/> と <see cref="LogRecord.StructuredData"/> を含めない。
/// </summary>
/// <param name="Id">レコード識別子。</param>
/// <param name="ReceivedAt">サーバが受信した時刻（UTC）。</param>
/// <param name="SourceAddress">送信元アドレス。</param>
/// <param name="SourcePort">送信元ポート。</param>
/// <param name="Protocol">受信リスナのトランスポートプロトコル。</param>
/// <param name="ParseStatus">解析結果（排他 3 値）。</param>
/// <param name="DeviceTimestamp">送信元が名乗った時刻（あれば）。</param>
/// <param name="Facility">syslog PRI の facility 分解値。</param>
/// <param name="Severity">syslog PRI の severity 分解値。</param>
/// <param name="Hostname">RFC 5424 HOSTNAME。</param>
/// <param name="AppName">RFC 5424 APP-NAME。</param>
/// <param name="ProcId">RFC 5424 PROCID。</param>
/// <param name="MsgId">RFC 5424 MSGID。</param>
/// <param name="Message">メッセージ本文（全文。M2 時点では先頭 N 文字への切り詰めは行わない）。</param>
public sealed record LogRecordSummary(
    long Id,
    DateTimeOffset ReceivedAt,
    string SourceAddress,
    int SourcePort,
    Protocol Protocol,
    ParseStatus ParseStatus,
    DateTimeOffset? DeviceTimestamp,
    int? Facility,
    int? Severity,
    string? Hostname,
    string? AppName,
    string? ProcId,
    string? MsgId,
    string? Message);
