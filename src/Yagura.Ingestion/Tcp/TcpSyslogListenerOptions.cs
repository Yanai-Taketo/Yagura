namespace Yagura.Ingestion.Tcp;

/// <summary>
/// <see cref="TcpSyslogListener"/> の構成。
/// </summary>
/// <remarks>
/// <see cref="Yagura.Ingestion.Udp.UdpSyslogListenerOptions"/> と構造を揃える（M4-1 依頼
/// 「既存コード把握: UDP の構造に揃えること」）。
/// </remarks>
public sealed class TcpSyslogListenerOptions
{
    /// <summary>
    /// 既定の bind アドレス。UDP と同じ既定に揃える——<c>::</c>（IPv6 ワイルドカード）を DualMode
    /// ソケットで bind し、IPv4・IPv6 の両方を単一ソケットで受信する（Issue #133。
    /// <see cref="TcpSyslogListener"/> の remarks・<see cref="Yagura.Ingestion.Net.DualStackBindAddress"/>
    /// 参照）。<c>0.0.0.0</c> を明示指定すると IPv4 のみに縮小される（後方互換の逃げ道）。
    /// </summary>
    public const string DefaultBindAddress = "::";

    /// <summary>syslog の既定 TCP ポート（RFC 6587 は UDP と同じ 514 番を前提とする実装が一般的）。</summary>
    public const int DefaultPort = 514;

    /// <summary>
    /// 同時接続数上限の既定値（暫定定数）。
    /// </summary>
    /// <remarks>
    /// architecture.md §9 M-14「TCP 同時接続数上限の既定値」は実測 + 想定送信元数を踏まえて
    /// 確定するとされており未確定。本実装は「読み取り停止中も接続は保持される」（§3.1）ため
    /// 資源の有限化として暫定値 256 を採用する（一般的な中小規模の syslog 送信元数を大きく
    /// 上回る余裕を見た値。M-14 の実測確定時に差し替える）。
    /// </remarks>
    public const int DefaultMaxConcurrentConnections = 256;

    /// <summary>
    /// 接続ごとのアイドルタイムアウトの既定値（暫定値。Issue #140）。
    /// </summary>
    /// <remarks>
    /// architecture.md §4.5 は「読み取り停止中も接続は保持されるため同時接続数に上限を設ける」
    /// ことは既に定めていたが、その上限自体が無言・低速な接続で埋まり続ける経路
    /// （slowloris 型の資源枯渇）への対処が抜けていた（Issue #140）。無通信のまま
    /// <see cref="DefaultMaxConcurrentConnections"/> 本の接続枠を占有され続けると、正常な
    /// 送信元の新規接続が拒否され続ける。本タイムアウトは「最後に 1 バイトでも読み取ってから
    /// この時間が経過したら切断する」アイドルタイムアウトとして実装する（読み取り中のメッセージ
    /// 転送速度そのものへの制限ではない）。既定値は、一般的な syslog 送信元の送信間隔
    /// （通常は数秒〜数分に 1 回以上、無イベントでも周期的なキープアライブ的送信を行う実装が
    /// 多い）に対して十分な余裕を持たせつつ、無言接続を有限時間で回収できる値として 5 分を
    /// 暫定採用する（M-14 の同時接続数上限の実測確定と合わせて再評価する候補——本件は
    /// architecture.md §9 の実測待ち一覧に未掲載のため、本 PR の最終報告で「暫定値」として
    /// 明示する）。
    /// </remarks>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// フレーミング進捗タイムアウトの既定値（仮値。実利用で確定する。PR #169 レビュー指摘 3 への
    /// オーナー決定 2026-07-09）。
    /// </summary>
    /// <remarks>
    /// アイドルタイムアウト（<see cref="DefaultIdleTimeout"/>）は「読み取りが起きない」接続を
    /// 回収するが、**バイトは届き続けているのに有効なメッセージが 1 件も確定しない接続**
    /// （低速トリクル・LF を送らないゴミデータ等）は常に読み取りが発生し続けるためアイドルに
    /// ならず、回収できない。再同期バイト数上限（<see cref="TcpFrameDecoderOptions.MaxResyncBytes"/>）
    /// と併せたもう 1 つの天井として、この時間内に有効なメッセージが 1 件も確定しない接続を
    /// 切断する。既定値 60 秒はオーナー指定範囲（30〜60 秒）の上端を選定——既定の 1 メッセージ
    /// 上限 64 KiB を 60 秒かけても確定できないのは 9 kbit/s 未満の病的な低速であり、輻輳した
    /// 回線経由等の正常な低速送信元を巻き込むリスクを最小化する側に倒した。有効なメッセージが
    /// 1 件確定するたびにタイマーはリセットされる。
    /// </remarks>
    public static readonly TimeSpan DefaultFramingProgressTimeout = TimeSpan.FromSeconds(60);

    /// <summary>bind するアドレス。既定は <see cref="DefaultBindAddress"/>。</summary>
    public string BindAddress { get; init; } = DefaultBindAddress;

    /// <summary>
    /// bind するポート。既定は <see cref="DefaultPort"/>。
    /// テストでは 0 を指定すると OS がポートを採番する（<see cref="TcpSyslogListener.BoundPort"/> 参照）。
    /// </summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>同時接続数上限。既定は <see cref="DefaultMaxConcurrentConnections"/>。</summary>
    public int MaxConcurrentConnections { get; init; } = DefaultMaxConcurrentConnections;

    /// <summary>1 接続・1 メッセージのバイト数上限。既定は <see cref="TcpFrameDecoderOptions.DefaultMaxMessageLength"/>。</summary>
    public int MaxMessageLength { get; init; } = TcpFrameDecoderOptions.DefaultMaxMessageLength;

    /// <summary>
    /// 接続ごとのアイドルタイムアウト。既定は <see cref="DefaultIdleTimeout"/>。
    /// 最後にバイトを読み取ってからこの時間が経過すると接続を切断する（Issue #140）。
    /// <see cref="TimeSpan.Zero"/> 以下を指定すると無効化する（無期限待機に戻る。主にテスト用途）。
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = DefaultIdleTimeout;

    /// <summary>
    /// 有効なメッセージが 1 件確定するごとにリセットされる、読み捨てバイト数の上限
    /// （再同期バイト数上限。オーナー決定 2026-07-09）。超過した接続は切断する。
    /// 既定は <see cref="TcpFrameDecoderOptions.DefaultMaxResyncBytes"/>。0 以下で無効化。
    /// </summary>
    public int MaxResyncBytes { get; init; } = TcpFrameDecoderOptions.DefaultMaxResyncBytes;

    /// <summary>
    /// フレーミング進捗タイムアウト。既定は <see cref="DefaultFramingProgressTimeout"/>。
    /// バイトは届いているのに有効なメッセージが 1 件も確定しないままこの時間が経過した接続を
    /// 切断する（オーナー決定 2026-07-09。有効なメッセージが確定するたびにリセット）。
    /// <see cref="TimeSpan.Zero"/> 以下を指定すると無効化する（主にテスト用途）。
    /// </summary>
    public TimeSpan FramingProgressTimeout { get; init; } = DefaultFramingProgressTimeout;
}
