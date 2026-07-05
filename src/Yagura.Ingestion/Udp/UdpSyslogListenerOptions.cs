namespace Yagura.Ingestion.Udp;

/// <summary>
/// <see cref="UdpSyslogListener"/> の構成。
/// </summary>
/// <remarks>
/// 本格的な設定基盤（JSON 設定ファイル・ウィザード反映）は M3 で行う。M2 時点は
/// この POCO をコードから直接組み立てる。
/// </remarks>
public sealed class UdpSyslogListenerOptions
{
    /// <summary>
    /// 既定の bind アドレス（すべてのインターフェース）。configuration.md での既定確定前の暫定値。
    /// </summary>
    public const string DefaultBindAddress = "0.0.0.0";

    /// <summary>
    /// syslog の既定 UDP ポート（RFC 5426）。
    /// </summary>
    public const int DefaultPort = 514;

    /// <summary>
    /// UDP 受信ソケットの受信バッファサイズ（<c>SO_RCVBUF</c>）の既定値（バイト）。
    /// architecture.md §9 M-2 の実機検証で確定した値。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>確定の根拠（2026-07-05・開発機実測。tools/Yagura.Bench/results/2026-07-05-dev-machine-rcvbuf/）</b>:
    /// 既定の OS バッファ（実測 65,536 バイト = 64 KiB）では UDP 破棄ゼロ上限が
    /// 約 20,000〜30,000 msg/sec（M7-2 実測）だったのに対し、本値（4 MiB）へ拡大すると
    /// 同一開発機で破棄ゼロ上限が大幅に上昇することを確認した（比較表は最終報告参照）。
    /// メモリ消費（プロセスあたり 4 MiB・単一 UDP リスナのみ保持）は常識的な範囲であり、
    /// 過大な確保による副作用（他プロセスへのメモリ圧迫）を避けつつ改善効果を得られる
    /// 値として選定した。
    /// </para>
    /// <para>
    /// <b>Windows の丸め挙動（実機確認）</b>: legacy-lessons.md A-1 は「Windows では約 1MB 超の
    /// 指定は setsockopt が失敗する」と記録していたが、本開発機（Windows 11 ARM64
    /// 10.0.26200・.NET 10.0.301）での実機検証では、64KB 超過はもちろん 1MB・4MB・16MB は
    /// おろか 2GiB-1 まで <see cref="System.Net.Sockets.Socket.ReceiveBufferSize"/> の setter が
    /// 例外を送出せず、読み戻し値も要求値と完全一致した（丸めは一切観測されなかった）。
    /// 旧リポジトリの記録は本環境では再現しないため、この設計は「丸めが起きても実効値を
    /// ログへ記録する」という防御的な実装（<see cref="UdpSyslogListener"/> 参照）を保持しつつ、
    /// 既定値の選定自体は 1MB 上限を前提にしない。
    /// </para>
    /// </remarks>
    public const int DefaultReceiveBufferBytes = 4 * 1024 * 1024;

    /// <summary>
    /// <see cref="ReceiveBufferBytes"/> の下限（バイト）。OS 既定値（Windows 実測 64 KiB）を
    /// 下回る指定は「受信バッファを拡大する」という設定項目の目的そのものに反するため、
    /// 不正値として扱う（configuration.md §1「既定値で継続」）。
    /// </summary>
    public const int MinReceiveBufferBytes = 64 * 1024;

    /// <summary>
    /// <see cref="ReceiveBufferBytes"/> の上限（バイト）。非現実的な巨大値（例: 誤入力による
    /// 桁違いの指定）を弾くための安全側の上限。実機検証では setsockopt 自体は 2GiB-1 まで
    /// 成功したが、単一 UDP ソケットに対する妥当な確保量として 256 MiB を上限とする
    /// （それ以上の値を要求する運用ニーズが確認されるまでの暫定的な安全弁）。
    /// </summary>
    public const int MaxReceiveBufferBytes = 256 * 1024 * 1024;

    /// <summary>
    /// bind するアドレス。既定は <see cref="DefaultBindAddress"/>（0.0.0.0 = すべてのインターフェース）。
    /// </summary>
    public string BindAddress { get; init; } = DefaultBindAddress;

    /// <summary>
    /// bind するポート。既定は <see cref="DefaultPort"/>。
    /// テストでは 0 を指定すると OS がポートを採番する
    /// （<see cref="UdpSyslogListener.BoundPort"/> で実際に束縛されたポートを取得できる）。
    /// </summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// UDP 受信ソケットの受信バッファサイズ（<c>SO_RCVBUF</c>。バイト単位）。既定は
    /// <see cref="DefaultReceiveBufferBytes"/>。OS 側ソケットバッファ溢れ（architecture.md §3.1・
    /// §4.2 M-2）への第一の緩和策。
    /// </summary>
    public int ReceiveBufferBytes { get; init; } = DefaultReceiveBufferBytes;
}
