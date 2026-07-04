using Yagura.Storage;

namespace Yagura.Ingestion.Udp;

/// <summary>
/// 受信段が読み取った 1 件の生データグラムを解析段へ渡すための封筒型
/// （architecture.md §2.1 受信段 → 解析段の Q1）。
/// </summary>
/// <remarks>
/// 受信段はこの型を組み立てるだけで、解析・書き込みは行わない（§2.1
/// 「受信段はソケットからの読み取りに専念する」）。
/// </remarks>
/// <param name="ReceivedAt">サーバが受信した時刻（UTC）。読み取りループがデータグラムを受け取った直後に刻印する。</param>
/// <param name="SourceAddress">送信元アドレス（文字列表現）。</param>
/// <param name="SourcePort">送信元ポート。</param>
/// <param name="Protocol">受信リスナのトランスポートプロトコル。M2 は <see cref="Yagura.Storage.Protocol.Udp"/> のみ。M4 で <see cref="Yagura.Storage.Protocol.Tcp"/> が加わる。</param>
/// <param name="Payload">受信したバイト列そのもの。</param>
/// <param name="Incomplete">
/// TCP 接続が切断された時点で、まだメッセージ境界（octet-counting の残長 / LF 区切りの
/// トレーラー）に到達していなかった読みかけデータであることを示す（database.md §2.1
/// 「不完全は解析失敗に優先する」）。UDP 由来は常に <c>false</c>。解析段（<see
/// cref="Yagura.Ingestion.Parsing.MinimalSyslogParser"/>）はこのフラグを最優先で見て
/// <see cref="Yagura.Storage.ParseStatus.Incomplete"/> を返す。
/// </param>
public sealed record RawDatagram(
    DateTimeOffset ReceivedAt,
    string SourceAddress,
    int SourcePort,
    Protocol Protocol,
    byte[] Payload,
    bool Incomplete = false);
