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
    /// 既定の bind アドレス。<c>::</c>（IPv6 ワイルドカード）を DualMode ソケットで bind し、
    /// IPv4・IPv6 の両方を単一ソケットで受信する（Issue #133。<see cref="UdpSyslogListener"/> の
    /// remarks・<see cref="Yagura.Ingestion.Net.DualStackBindAddress"/> 参照）。
    /// </summary>
    /// <remarks>
    /// <b>後方互換の逃げ道</b>: <c>BindAddress</c> に明示的に <c>0.0.0.0</c>（IPv4 ワイルドカード）
    /// を指定した場合は、旧来どおり IPv4 単独ソケットで bind する（IPv6 を受けない）。
    /// v0.1〜v0.2 で <c>0.0.0.0</c> が既定だった環境からのアップグレードで、手動設定ファイルに
    /// 明示 <c>0.0.0.0</c> が残っている場合の挙動を変えないための設計判断。
    /// </remarks>
    public const string DefaultBindAddress = "::";

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
    /// <b>確定の根拠（2026-07-05・開発機実測。tools/Yagura.Bench/results/2026-07-05-dev-machine-rcvbuf/
    /// README.md に比較表・分析を記録）</b>: バッファ {64KiB(OS 既定)・1MiB・4MiB・16MiB} ×
    /// レート {20000, 30000, 40000, 60000 msg/sec} の持続負荷（SustainedZeroDrop）を実測した結果、
    /// **OS ソケットバッファでの無計測ロス（導出値）は 1MiB 以上のすべてのセルで厳密にゼロ**に
    /// なった一方、64KiB（OS 既定）では総損失の相当割合（レートに応じて約 24〜38%）が
    /// この無計測ロスだった。**1MiB を超えて 4MiB・16MiB へ拡大しても、この指標にも総損失・
    /// Q1 破棄件数にも一貫した追加の改善は確認できなかった**——律速要因が OS バッファから
    /// Q1（内部キュー。容量固定 1024。M-1）へ移った後は、受信バッファをさらに拡大しても
    /// Q1 の容量自体は変わらないため。バーストシナリオ（BurstQ1Drop・一斉送出 20000 通）でも
    /// 同じ構図を確認した: 64KiB では総損失の 86.7%（15,395/17,750 件）が OS レベルの
    /// 無計測ロスだったが、4MiB ではこのロスが完全にゼロになり、同水準の損失が Q1 破棄という
    /// 「発生箇所別カウンタに必ず計上される」形に転化した（architecture.md §3.1 の設計原則が
    /// 実際に機能することを示す一次データ）。以上から、**「OS レベル無計測ロスの解消」という
    /// 主目的を達成する最小の値**として本値（1 MiB）を既定とした——4MiB・16MiB は同じ効果に
    /// 対して追加のメモリ確保（既定の 4 倍・16 倍）を要求するのみで、実測上の恩恵が
    /// 確認できなかった。
    /// </para>
    /// <para>
    /// <b>実測の限界</b>: 本キャンペーンは開発機 1 台・各セル 1 回の実測であり、20000〜40000
    /// msg/sec 帯の破棄件数には実行間の揺らぎが確認されている（同一セルの再試行で内訳が
    /// 変動することを別途確認済み。README.md「実行間の揺らぎ」参照）。基準環境・複数回試行での
    /// 再確認は architecture.md M-6（基準環境の確定）に委ねる。
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
    public const int DefaultReceiveBufferBytes = 1024 * 1024;

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
    /// bind するアドレス。既定は <see cref="DefaultBindAddress"/>（<c>::</c> = DualMode による
    /// IPv4/IPv6 両対応の全インターフェース）。<c>0.0.0.0</c> を明示指定すると IPv4 のみに
    /// 縮小される（<see cref="DefaultBindAddress"/> の remarks 参照）。
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
