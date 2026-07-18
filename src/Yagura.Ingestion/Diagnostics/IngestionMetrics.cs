using System.Diagnostics.Metrics;

namespace Yagura.Ingestion.Diagnostics;

/// <summary>
/// パイプラインの計測点（architecture.md §3.1 カウンタ 8 種の表・§4.1 発生箇所別ドロップカウンタ。
/// ADR-0002 決定 4）。
/// </summary>
/// <remarks>
/// <para>
/// <b>命名規則（M4-4 で確定）</b>: <b>単一 Meter <c>Yagura</c> + 計器名
/// <c>yagura.&lt;領域&gt;.&lt;事象&gt;</c></b> 方式を採用する。
/// </para>
/// <para>
/// <b>比較検討した選択肢と選定理由</b>: 対抗案は「発生箇所ごとにタグ付けした単一カウンタ
/// （例: <c>yagura.dropped.total</c> + <c>site</c> タグで 8 種を区別）」だった。
/// OpenTelemetry の計装ガイドライン（opentelemetry.io/docs/specs/semconv/general/metrics/、
/// 確認日 2026-07-05）は「同種の事象の次元展開（例: HTTP メソッド別のリクエスト数）は
/// タグで表現し、計器の乱立を避ける」ことを推奨している——しかし本表の 8 種は「同じ事象の
/// 異なる次元」ではなく、<b>意味論そのものが異なる別々の事象</b>（内部バッファ破棄・スプール
/// 退避・TCP 接続拒否等はそれぞれ発生条件・対処方針が異なる）である。単一カウンタ + タグ方式は
/// (i) 「損失は必ずどれかのカウンタに計上される」（§4.1）という原則の検証が「タグ値の網羅」
/// という間接的な確認になり事故りやすい、(ii) UI-7（ui.md）の試用報告様式が「カウンタ識別子
/// ——開発用語側のキー」を 1 対 1 で要求しており、タグ付き単一カウンタはこの 1 対 1 対応を
/// 崩す、という 2 点で本製品に適さないと判断した。既存 6 種もすでに計器名分離方式で実装済み
/// であり（M2〜M4-3）、変更の実質的な理由もない。
/// </para>
/// <para>
/// <b>Meter のスコープ</b>: M2〜M4-3 は Meter 名を <c>Yagura.Ingestion</c>（アセンブリ名）
/// としていたが、M4-4 でメタデータ領域（Yagura.Host 側）・OS 統計突合ゲージを追加するにあたり、
/// 「観測点はアセンブリ境界と無関係に 1 つの計測空間として扱う」という設計（ADR-0002 決定 4
/// 「メトリクスは…出力先…は差し替え可能にする」＝計測点を一元的に列挙・購読できることが前提）
/// に合わせ、<b>単一 Meter 名 <c>Yagura</c></b> に統合する。試用報告（ui.md UI-7）・M8 の状態画面は
/// 「1 つの Meter を購読すれば全カウンタ・ゲージが揃う」ことを前提にできる。
/// </para>
/// <para>
/// M2 で「内部バッファ破棄」、M4-1 で「TCP 接続拒否」、M4-3 で「スプール退避」「スプール書込
/// 失敗」「スプール破棄」「永続化失敗」を追加した。M4-4 で「流量制御破棄」（M4-4 時点は挿入点
/// のみ。Issue #260 で <see cref="Yagura.Ingestion.FlowControl.TokenBucketIngressGate"/> による
/// 判定・破棄が実装され、実値を刻む計器になった）と、
/// OS 統計突合ゲージ（§4.2。のち ADR-0016 決定 3 で撤去）を追加した。Issue #143・#140（syslog 実務者ペルソナの深掘り
/// レビューで発見された、1 メッセージの逸脱による接続全損・アイドル接続の資源枯渇の 2 件）の
/// 対応で「TCP 接続断」（当初計画の 12 種の一つ）・「TCP 接続アイドルタイムアウト」・
/// 「TCP メッセージ破棄（上限超過）」（後者 2 つは新規）を追加した。PR #169 レビュー指摘 3 への
/// オーナー決定（2026-07-09）で「TCP 接続再同期上限」「TCP フレーミング進捗タイムアウト」
/// （いずれも TCP 接続断の内訳。新規）を追加した。残り（解析失敗
/// （保存済み）・TLS ハンドシェイク失敗・TCP 不完全メッセージ）は該当機能（TLS 受信等）の
/// 実装時に追加する。
/// </para>
/// <para>
/// Issue #142 で「UDP 受信エラー」を追加した（<see cref="RecordUdpReceiveError"/>）。
/// <see cref="Yagura.Ingestion.Udp.UdpSyslogListener"/> の <c>ReceiveAsync</c> が
/// <see cref="System.Net.Sockets.SocketException"/> で失敗した回数の診断用カウンタである。
/// 他の 7 種（<see cref="IngestionCounterSnapshot"/> が保持する「発生箇所別ドロップカウンタ」
/// §4.1）とは異なり、個々の失敗が必ずしもデータグラム損失と 1 対 1 対応するとは限らない
/// （ネットワーク環境依存の一過性エラーを含み得る）ため、再起動をまたぐ累積永続化（§4.3・
/// <see cref="SeedCumulativeCounters"/>・<see cref="SnapshotCumulativeCounters"/>）の対象には
/// 含めない——プロセス内累積のみ（Web 層の逆引き解決カウンタと同じ扱い。§4.1.1 末尾の注記参照）。
/// </para>
/// <para>
/// <b>Issue #201「スプール末尾破損破棄」</b>: <c>SpoolDrainCoordinator.DrainSegmentAsync</c> が
/// スプールセグメント末尾の破損（<c>corruptTailDetected</c>）を検出した際、従来はその破棄分が
/// どのカウンタにも計上されないままセグメントが削除されていた——「カウンタに計上されない喪失は
/// 重大」（§3.1）に反する観測ギャップだった。<see cref="RecordSpoolCorruptTailDiscarded"/> で
/// 計上する。<b>単位はレコード数ではなくバイト数</b>にした——破損した末尾はフレーム境界が
/// 保証されない（<c>SpoolSegmentReader</c> のクラス remarks 参照）ため、そこに何件のレコードが
/// あったはずかを数える手段が原理的に存在しない（境界不明のバイト列を再同期して数えようとすると
/// フレーム先頭の誤認識リスクを負う）。他 7 種の「発生箇所別ドロップカウンタ」（§4.1）は他の
/// カウンタと同じくメタデータ領域への永続化（§4.3・<see cref="SeedCumulativeCounters"/>・
/// <see cref="SnapshotCumulativeCounters"/>）の対象に含める——「サーバに届いた後、回収不能な形で
/// 失われた」という点は他の損失カウンタと同質であり、UDP 受信エラー・逆引き解決カウンタのような
/// 「プロセス内累積のみで足りる診断用カウンタ」とは性質が異なるため。
/// </para>
/// </remarks>
public sealed class IngestionMetrics : IDisposable
{
    /// <summary>
    /// このコンポーネントが使用する <see cref="Meter"/> の名前（M4-4 で確定。命名規則は本クラスの
    /// remarks 参照）。
    /// </summary>
    public const string MeterName = "Yagura";

    private readonly Meter _meter;
    private readonly Counter<long> _internalBufferDropped;
    private readonly Counter<long> _tcpConnectionRejected;
    private readonly Counter<long> _spoolEvacuated;
    private readonly Counter<long> _spoolWriteFailed;
    private readonly Counter<long> _spoolDiscarded;
    private readonly Counter<long> _persistenceFailed;
    private readonly Counter<long> _flowControlDropped;
    private readonly Counter<long> _udpReceiveError;
    private readonly Counter<long> _tcpConnectionClosed;
    private readonly Counter<long> _tcpConnectionIdleTimeout;
    private readonly Counter<long> _tcpMessageDiscardedOversized;
    private readonly Counter<long> _tcpConnectionResyncLimitExceeded;
    private readonly Counter<long> _tcpConnectionFramingTimeout;
    private readonly Counter<long> _spoolCorruptTailDiscarded;
    private readonly Counter<long> _tlsHandshakeFailure;
    private readonly Counter<long> _parseFailedSaved;
    private readonly Counter<long> _tcpIncompleteMessage;

    /// <summary>
    /// TLS ハンドシェイク失敗カウンタ（<see cref="RecordTlsHandshakeFailure"/>）の <c>source_address</c>
    /// タグの distinct 値の上限。送信元 IP は認証成立前に計上される攻撃者制御の次元であり、無制限の
    /// タグ基数は集約 exporter のメモリを圧迫し得るため上限を設ける。上限到達後の新規送信元は
    /// <see cref="TlsHandshakeFailureOverflowSource"/> へ畳む——「ほぼ全送信元が失敗」の観測は overflow
    /// バケットの増加として現れ、SEC-D3 の送信元別脱落確認の目的は損なわれない（security.md §6・
    /// architecture.md §4.1.1）。
    /// </summary>
    public const int MaxTlsHandshakeFailureSourceCardinality = 256;

    /// <summary>カーディナリティ上限到達後の新規送信元を畳み込む集約タグ値。</summary>
    public const string TlsHandshakeFailureOverflowSource = "(other)";

    private readonly object _tlsSourceTagGate = new();
    private readonly HashSet<string> _tlsHandshakeFailureSources = new(StringComparer.Ordinal);

    // architecture.md §4.3「前回までの累積 + 今回プロセス分」の合成を行うための自前の
    // 累積保持（§4.3 実装ノート参照）。System.Diagnostics.Metrics.Counter<T> は加算専用の
    // 書き込み API のみを公開し、現在の累積値を読み戻す標準 API を持たない
    // （MeterListener で購読する経路はあるが、購読の生存期間管理がホスト側の定期永続化
    // タイマーと二重の状態管理になり複雑化するため採らなかった）。そのため本クラス自身が
    // 各 Record*() 呼び出しと同時に Interlocked でプロセス内累積値を保持し、
    // メタデータ領域（Yagura.Host.Observability）はこの値をスナップショットとして読み出す
    // だけでよい設計にした。起動時の「前回までの累積」の引き継ぎは
    // <see cref="SeedCumulativeCounters"/> で行う（コンストラクタ後・計測開始前に 1 回だけ
    // 呼ぶ想定）。
    private long _internalBufferDroppedTotal;
    private long _tcpConnectionRejectedTotal;
    private long _spoolEvacuatedTotal;
    private long _spoolWriteFailedTotal;
    private long _spoolDiscardedTotal;
    private long _persistenceFailedTotal;
    private long _flowControlDroppedTotal;
    private long _tcpConnectionClosedTotal;
    private long _tcpConnectionIdleTimeoutTotal;
    private long _tcpMessageDiscardedOversizedTotal;
    private long _tcpConnectionResyncLimitExceededTotal;
    private long _tcpConnectionFramingTimeoutTotal;
    private long _spoolCorruptTailDiscardedTotal;

    public IngestionMetrics()
    {
        _meter = new Meter(MeterName);

        // architecture.md §3.1・§4.1「内部バッファ破棄」: Q1（UDP 由来）が満杯で解析段へ渡せず
        // 破棄した件数。
        _internalBufferDropped = _meter.CreateCounter<long>(
            "yagura.ingestion.internal_buffer.dropped",
            unit: "{datagram}",
            description: "Q1 (UDP 由来) が満杯のため解析段へ渡せず破棄したデータグラム数。");

        // architecture.md §3.1・§4.1「TCP 接続拒否」: 同時接続数上限（§3.1・M-14）到達により
        // 新規接続を拒否した件数。既存接続を守るための有限化が働いていることの確認に使う。
        _tcpConnectionRejected = _meter.CreateCounter<long>(
            "yagura.ingestion.tcp_connection.rejected",
            unit: "{connection}",
            description: "TCP 同時接続数上限到達により拒否した新規接続数。");

        // architecture.md §3.1・§4.1「スプール退避」: Q2 溢れ・書き込み失敗/タイムアウトにより
        // スプールへ退避した件数（損失ではない。飽和の予兆シグナル。§3.1）。
        _spoolEvacuated = _meter.CreateCounter<long>(
            "yagura.ingestion.spool.evacuated",
            unit: "{record}",
            description: "Q2 溢れ・書き込み失敗/タイムアウトによりスプールへ退避した件数。");

        // architecture.md §3.1・§4.1「スプール書込失敗」: スプール追記をリトライしても書き込めず
        // 破棄した件数（ディスク障害等）。
        _spoolWriteFailed = _meter.CreateCounter<long>(
            "yagura.ingestion.spool.write_failed",
            unit: "{record}",
            description: "スプール追記がリトライ後も失敗し破棄した件数。");

        // architecture.md §3.1・§4.1「スプール破棄」: スプール上限到達により新規到着分を破棄した
        // 件数（§3.2.3）。
        _spoolDiscarded = _meter.CreateCounter<long>(
            "yagura.ingestion.spool.discarded",
            unit: "{record}",
            description: "スプール上限到達により新規破棄した件数。");

        // architecture.md §3.1・§4.1「スプール末尾破損破棄」（Issue #201 で追加）: drain がスプール
        // セグメント末尾の破損（corruptTailDetected）を検出し、回収不能として読み捨てたバイト数。
        // 単位はレコード数ではなくバイト数——破損した末尾はフレーム境界が保証されないため
        // レコード数を数える手段が原理的に存在しない（本クラス remarks 参照）。
        _spoolCorruptTailDiscarded = _meter.CreateCounter<long>(
            "yagura.ingestion.spool.corrupt_tail_discarded_bytes",
            unit: "By",
            description: "スプールセグメント末尾の破損検出により、回収不能として読み捨てたバイト数" +
                "（レコード単位では数えられないためバイト数で計上。Issue #201）。");

        // architecture.md §3.1・§4.1「永続化失敗」: リトライとスプール退避でも救えず失われた件数
        // （スプールなし縮退中の喪失を含む。§1.2）。
        _persistenceFailed = _meter.CreateCounter<long>(
            "yagura.ingestion.persistence.failed",
            unit: "{record}",
            description: "リトライ・スプール退避でも救えず失われた件数（スプールなし縮退中の喪失を含む）。");

        // architecture.md §3.1・§4.1「流量制御破棄」（M4-4 で枠を追加、Issue #260 で実装）:
        // 送信元単位の流量制御（§3.3。TokenBucketIngressGate）が拒否した件数。挿入点
        // （IIngressGate.ShouldAdmit の呼び出し元 = 各リスナ）で計上する
        // （「発火は必ず計測される」§3.3）。opt-out 構成（NoopIngressGate）では 0 のまま推移する。
        _flowControlDropped = _meter.CreateCounter<long>(
            "yagura.ingestion.flow_control.dropped",
            unit: "{datagram}",
            description: "送信元単位の流量制御により破棄した件数（opt-out 構成では常に 0）。");

        // architecture.md §4.1「UDP 受信エラー」（Issue #142 で追加）: UDP 受信ソケットの
        // ReceiveAsync が SocketException で失敗した回数。個々の失敗が必ずしもデータグラム損失と
        // 1 対 1 対応するとは限らない診断用カウンタのため、他 7 種とは異なりプロセス内累積のみ
        // （本クラス remarks 参照。再起動をまたぐ永続化の対象外）。
        _udpReceiveError = _meter.CreateCounter<long>(
            "yagura.ingestion.udp.receive_error",
            unit: "{error}",
            description: "UDP 受信ソケットの ReceiveAsync が SocketException で失敗した回数" +
                "（プロセス内累積。個々のケースが実データ損失と一致するとは限らない診断用カウンタ）。");

        // architecture.md §4.1・§4.5「TCP 接続断」（Issue #140）: TCP 接続が切断された回数
        // （理由を問わない——正常シャットダウン・異常切断・停止要求・再同期不能な破損・
        // アイドルタイムアウトのいずれも含む）。損失ではなく解釈の手がかり。
        _tcpConnectionClosed = _meter.CreateCounter<long>(
            "yagura.ingestion.tcp_connection.closed",
            unit: "{connection}",
            description: "TCP 接続が切断された回数（理由を問わない。損失ではなく解釈の手がかり）。");

        // architecture.md §4.5「TCP 接続アイドルタイムアウト」（Issue #140 で新設）:
        // アイドルタイムアウト（TcpSyslogListenerOptions.IdleTimeout）により切断した接続数
        // （§4.1「TCP 接続断」の内訳の一種として、無言接続の資源回収が働いていることを
        // 個別に確認できるよう分離する）。
        _tcpConnectionIdleTimeout = _meter.CreateCounter<long>(
            "yagura.ingestion.tcp_connection.idle_timeout",
            unit: "{connection}",
            description: "アイドルタイムアウトにより切断した TCP 接続数（TCP 接続断の内訳）。");

        // architecture.md §4.5「TCP メッセージ破棄（上限超過）」（Issue #143 で新設）:
        // 1 メッセージのバイト数上限（TcpFrameDecoderOptions.MaxMessageLength）超過により、
        // 当該メッセージのみを破棄した件数（接続は維持する。TcpFrameDecoder.
        // OversizedMessagesDiscardedCount 参照）。
        _tcpMessageDiscardedOversized = _meter.CreateCounter<long>(
            "yagura.ingestion.tcp_message.oversized_discarded",
            unit: "{message}",
            description: "1 メッセージのサイズ上限超過により、当該メッセージのみを破棄した件数（接続は維持）。");

        // architecture.md §4.5「TCP 接続再同期上限」（PR #169 レビュー指摘 3 へのオーナー決定
        // 2026-07-09 で新設）: 有効なメッセージが 1 件も確定しないまま読み捨てたバイト数が上限
        // （TcpFrameDecoderOptions.MaxResyncBytes）を超えて切断した接続数（TCP 接続断の内訳）。
        _tcpConnectionResyncLimitExceeded = _meter.CreateCounter<long>(
            "yagura.ingestion.tcp_connection.resync_limit_exceeded",
            unit: "{connection}",
            description: "再同期バイト数上限の超過により切断した TCP 接続数（TCP 接続断の内訳）。");

        // architecture.md §4.5「TCP フレーミング進捗タイムアウト」（同上で新設）: バイトは
        // 届いているのに有効なメッセージが 1 件も確定しないまま一定時間
        // （TcpSyslogListenerOptions.FramingProgressTimeout）が経過して切断した接続数
        // （TCP 接続断の内訳。低速トリクル対策——アイドルタイムアウトとは別軸）。
        _tcpConnectionFramingTimeout = _meter.CreateCounter<long>(
            "yagura.ingestion.tcp_connection.framing_timeout",
            unit: "{connection}",
            description: "有効メッセージが確定しないまま一定時間が経過し切断した TCP 接続数（TCP 接続断の内訳）。");

        // architecture.md §4.1「TLS ハンドシェイク失敗」（Issue #137 で新設）: TLS 受信（RFC 5425。
        // opt-in）の TLS ハンドシェイク確立失敗数。送信元別に計上する——証明書期限切れ時に送信側が
        // 検証拒否した場合の一次シグナルであり（security.md §6）、「どの送信元が脱落しているか」が
        // 初動解析の手がかりになるため、本計器は既存 7 種と異なり送信元アドレスをタグに持つ
        // （§4.1.1 の「意味論の異なる別々の事象」原則はそのまま——本計器はタグ付き単一カウンタ方式
        // への転換ではなく、TLS ハンドシェイク失敗という単一の事象を送信元次元で展開したもの）。
        _tlsHandshakeFailure = _meter.CreateCounter<long>(
            "yagura.ingestion.tcp.tls_handshake_failure",
            unit: "{connection}",
            description: "TLS 受信（RFC 5425）の TLS ハンドシェイク確立失敗数（送信元別。証明書期限切れ時の" +
                "送信側拒否の検出に使う。security.md §6）。");

        // architecture.md §4.1「解析失敗（保存済み）」（Issue #270）: RFC 3164 / RFC 5424 の解析に
        // 失敗し、生データのまま解析失敗の印を付けて保存したレコード数（ParseStatus.ParseFailed）。
        // 損失ではない——「不正形式の頻発はそれ自体が観測対象」（§2.1）。UDP 受信エラー・TLS
        // ハンドシェイク失敗と同じ診断用カウンタとして、プロセス内累積のみ（再起動をまたぐ永続化
        // ＝損失台帳の対象外——保存済みで失われていないため）。
        _parseFailedSaved = _meter.CreateCounter<long>(
            "yagura.ingestion.parse.failed_saved",
            unit: "{record}",
            description: "解析に失敗し生データのまま保存したレコード数（損失ではない。§4.1。診断用・プロセス内累積）。");

        // architecture.md §4.1「TCP 不完全メッセージ」（Issue #270）: TCP 切断時に解析途中だった
        // 不完全メッセージ数（ParseStatus.Incomplete）。生データのまま印を付けて保存する（損失では
        // ない。database.md §2.1 の排他 3 値のうち不完全）。解析失敗（保存済み）と同じ診断用
        // カウンタ扱い（プロセス内累積のみ）。
        _tcpIncompleteMessage = _meter.CreateCounter<long>(
            "yagura.ingestion.tcp_message.incomplete",
            unit: "{message}",
            description: "TCP 切断時に解析途中だった不完全メッセージ数（生データのまま保存。§4.1。診断用・プロセス内累積）。");

        // OS 統計突合ゲージ（yagura.os.udp.*）は ADR-0016 決定 3 で撤去した（検証済み環境で
        // 受信・破棄を反映しないことが実測確定したため。architecture.md §4.2）。再導入は
        // 同 ADR 再評価トリガ (d) 陽性時の amendment を要する——無自覚な復活は
        // IngestionMetricsOsUdpAbsenceTests が回帰として防ぐ。
    }

    /// <summary>
    /// Q1（UDP 由来）の溢れによる破棄を 1 件計上する。
    /// </summary>
    public void RecordInternalBufferDropped()
    {
        _internalBufferDropped.Add(1);
        Interlocked.Increment(ref _internalBufferDroppedTotal);
    }

    /// <summary>
    /// TCP 同時接続数上限到達による新規接続拒否を 1 件計上する。
    /// </summary>
    public void RecordTcpConnectionRejected()
    {
        _tcpConnectionRejected.Add(1);
        Interlocked.Increment(ref _tcpConnectionRejectedTotal);
    }

    /// <summary>
    /// スプールへの退避を 1 件計上する（損失ではない。§4.1）。<paramref name="reason"/> を
    /// <c>reason</c> タグとして付与し、退避契機（容量 / 時間 / 停止時）を判別可能にする
    /// （M-7 の残作業。Issue #271）。累積総数（<see cref="SnapshotCumulativeCounters"/> が返す
    /// 永続化対象）は契機によらず単一の総和のまま——タグは実行時の計器（ダッシュボード・試用報告）
    /// の次元展開であり、損失台帳の「1 事象 = 1 カウンタ」対応は崩さない（§4.1.1 の TLS ハンド
    /// シェイク失敗と同じ「単一事象の次元展開」の位置づけ）。
    /// </summary>
    public void RecordSpoolEvacuated(SpoolEvacuationReason reason)
    {
        _spoolEvacuated.Add(1, new KeyValuePair<string, object?>("reason", ToReasonTag(reason)));
        Interlocked.Increment(ref _spoolEvacuatedTotal);
    }

    private static string ToReasonTag(SpoolEvacuationReason reason) => reason switch
    {
        SpoolEvacuationReason.Q2Overflow => "q2_overflow",
        SpoolEvacuationReason.WriteTimeout => "write_timeout",
        SpoolEvacuationReason.Shutdown => "shutdown",
        _ => "unknown",
    };

    /// <summary>
    /// スプール追記の失敗（リトライ後破棄）を 1 件計上する。
    /// </summary>
    public void RecordSpoolWriteFailed()
    {
        _spoolWriteFailed.Add(1);
        Interlocked.Increment(ref _spoolWriteFailedTotal);
    }

    /// <summary>
    /// スプール上限到達による破棄を 1 件計上する（§3.2.3）。
    /// </summary>
    public void RecordSpoolDiscarded()
    {
        _spoolDiscarded.Add(1);
        Interlocked.Increment(ref _spoolDiscardedTotal);
    }

    /// <summary>
    /// スプールセグメント末尾の破損検出により読み捨てたバイト数を計上する（§3.2.1・§4.1。
    /// Issue #201）。<paramref name="discardedBytes"/> は 1 回の検出で読み捨てたバイト数
    /// （0 以下は何も加算しない——corruptTailDetected が <c>false</c> の呼び出しを防御する）。
    /// </summary>
    public void RecordSpoolCorruptTailDiscarded(long discardedBytes)
    {
        if (discardedBytes <= 0)
        {
            return;
        }

        _spoolCorruptTailDiscarded.Add(discardedBytes);
        Interlocked.Add(ref _spoolCorruptTailDiscardedTotal, discardedBytes);
    }

    /// <summary>
    /// リトライ・スプール退避でも救えなかった永続化失敗を 1 件計上する
    /// （スプールなし縮退中の喪失を含む。§1.2・§4.1）。
    /// </summary>
    public void RecordPersistenceFailed()
    {
        _persistenceFailed.Add(1);
        Interlocked.Increment(ref _persistenceFailedTotal);
    }

    /// <summary>
    /// 流量制御による破棄を 1 件計上する（§3.3・§4.1。TokenBucketIngressGate の拒否時に
    /// 各リスナが呼ぶ。opt-out 構成の NoopIngressGate では発火しない）。
    /// </summary>
    public void RecordFlowControlDropped()
    {
        _flowControlDropped.Add(1);
        Interlocked.Increment(ref _flowControlDroppedTotal);
    }

    /// <summary>
    /// UDP 受信ソケットの受信エラー（<see cref="System.Net.Sockets.SocketException"/>）を
    /// 1 件計上する（Issue #142）。プロセス内累積のみで、再起動をまたぐ永続化
    /// （<see cref="SeedCumulativeCounters"/>・<see cref="SnapshotCumulativeCounters"/>）の
    /// 対象外（本クラス remarks 参照）。
    /// </summary>
    public void RecordUdpReceiveError()
    {
        _udpReceiveError.Add(1);
    }

    /// <summary>
    /// TCP 接続の切断を 1 件計上する（理由を問わない。§4.5「TCP 接続断」。Issue #140）。
    /// アイドルタイムアウトによる切断は本カウンタに加え <see cref="RecordTcpConnectionIdleTimeout"/>
    /// も併せて計上する。
    /// </summary>
    public void RecordTcpConnectionClosed()
    {
        _tcpConnectionClosed.Add(1);
        Interlocked.Increment(ref _tcpConnectionClosedTotal);
    }

    /// <summary>
    /// アイドルタイムアウトによる TCP 接続切断を 1 件計上する（§4.5。Issue #140）。
    /// </summary>
    public void RecordTcpConnectionIdleTimeout()
    {
        _tcpConnectionIdleTimeout.Add(1);
        Interlocked.Increment(ref _tcpConnectionIdleTimeoutTotal);
    }

    /// <summary>
    /// 1 メッセージのサイズ上限超過による破棄を 1 件計上する（接続は維持。§4.5。Issue #143）。
    /// </summary>
    public void RecordTcpMessageDiscardedOversized()
    {
        _tcpMessageDiscardedOversized.Add(1);
        Interlocked.Increment(ref _tcpMessageDiscardedOversizedTotal);
    }

    /// <summary>
    /// 再同期バイト数上限の超過による TCP 接続切断を 1 件計上する
    /// （§4.5。PR #169 レビュー指摘 3 へのオーナー決定 2026-07-09）。
    /// </summary>
    public void RecordTcpConnectionResyncLimitExceeded()
    {
        _tcpConnectionResyncLimitExceeded.Add(1);
        Interlocked.Increment(ref _tcpConnectionResyncLimitExceededTotal);
    }

    /// <summary>
    /// フレーミング進捗タイムアウトによる TCP 接続切断を 1 件計上する
    /// （§4.5。PR #169 レビュー指摘 3 へのオーナー決定 2026-07-09）。
    /// </summary>
    public void RecordTcpConnectionFramingTimeout()
    {
        _tcpConnectionFramingTimeout.Add(1);
        Interlocked.Increment(ref _tcpConnectionFramingTimeoutTotal);
    }

    /// <summary>
    /// TLS 受信（RFC 5425。opt-in）の TLS ハンドシェイク確立失敗を 1 件計上する（Issue #137）。
    /// <paramref name="sourceAddress"/> を <c>source_address</c> タグとして付与する（送信元別の脱落確認。
    /// security.md §6）。ただしタグの distinct 値は <see cref="MaxTlsHandshakeFailureSourceCardinality"/>
    /// までに有界化し、超過分は <see cref="TlsHandshakeFailureOverflowSource"/> へ集約する——送信元 IP は
    /// 認証成立前の攻撃者制御次元であり、無制限のタグ基数が集約 exporter のメモリを圧迫するのを防ぐ。
    /// UDP 受信エラー・逆引き解決カウンタと同じ「診断用カウンタ」の扱い——個々の失敗が必ずしも
    /// メッセージ損失と 1 対 1 対応するとは限らないため、プロセス内累積のみで再起動をまたぐ
    /// 永続化（<see cref="SeedCumulativeCounters"/>・<see cref="SnapshotCumulativeCounters"/>）の
    /// 対象には含めない。
    /// </summary>
    public void RecordTlsHandshakeFailure(string sourceAddress)
    {
        var tagValue = ResolveBoundedSourceTag(sourceAddress ?? "unknown");
        _tlsHandshakeFailure.Add(1, new KeyValuePair<string, object?>("source_address", tagValue));
    }

    /// <summary>
    /// <c>source_address</c> タグ値を有界化して返す。既知の送信元、または上限未満なら送信元をそのまま
    /// 使い（新規は追跡集合へ記録）、上限到達後の新規送信元は <see cref="TlsHandshakeFailureOverflowSource"/>
    /// に畳む。追跡集合も上限で有界。ハンドシェイク失敗は低頻度のため単純なロックで直列化してよい。
    /// </summary>
    private string ResolveBoundedSourceTag(string source)
    {
        lock (_tlsSourceTagGate)
        {
            if (_tlsHandshakeFailureSources.Contains(source))
            {
                return source;
            }

            if (_tlsHandshakeFailureSources.Count < MaxTlsHandshakeFailureSourceCardinality)
            {
                _tlsHandshakeFailureSources.Add(source);
                return source;
            }

            return TlsHandshakeFailureOverflowSource;
        }
    }

    /// <summary>
    /// 解析に失敗し生データのまま保存したレコードを 1 件計上する（§4.1「解析失敗（保存済み）」。
    /// Issue #270）。損失ではない診断用カウンタのため、プロセス内累積のみ（再起動をまたぐ永続化の
    /// 対象外）。
    /// </summary>
    public void RecordParseFailedSaved()
    {
        _parseFailedSaved.Add(1);
    }

    /// <summary>
    /// TCP 切断時に解析途中だった不完全メッセージを 1 件計上する（§4.1「TCP 不完全メッセージ」。
    /// Issue #270）。損失ではない診断用カウンタのため、プロセス内累積のみ（再起動をまたぐ永続化の
    /// 対象外）。
    /// </summary>
    public void RecordTcpIncompleteMessage()
    {
        _tcpIncompleteMessage.Add(1);
    }

    /// <summary>
    /// 起動時、メタデータ領域（§4.3）から読み込んだ前回までの累積値を引き継ぐ
    /// （「前回までの累積 + 今回プロセス分」の合成。Counter&lt;T&gt; 自体は加算専用で
    /// 初期値を設定する API を持たないため、本クラス自身が保持する
    /// <c>*Total</c> フィールドへ前回値を種として設定し、以後の <c>Record*()</c> 呼び出しが
    /// その上に積み増す形にする）。<see cref="SnapshotCumulativeCounters"/> で読み出す値は
    /// 常にこの合成後の値になる。パイプライン開始（受信開始）より前、コンストラクタ直後の
    /// 1 回だけ呼び出す想定——呼び出しは加算ではなく上書きであり、2 回呼ぶと前回分が
    /// 二重に積まれる。
    /// </summary>
    public void SeedCumulativeCounters(IngestionCounterSnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        Interlocked.Exchange(ref _internalBufferDroppedTotal, previous.InternalBufferDropped);
        Interlocked.Exchange(ref _tcpConnectionRejectedTotal, previous.TcpConnectionRejected);
        Interlocked.Exchange(ref _spoolEvacuatedTotal, previous.SpoolEvacuated);
        Interlocked.Exchange(ref _spoolWriteFailedTotal, previous.SpoolWriteFailed);
        Interlocked.Exchange(ref _spoolDiscardedTotal, previous.SpoolDiscarded);
        Interlocked.Exchange(ref _persistenceFailedTotal, previous.PersistenceFailed);
        Interlocked.Exchange(ref _flowControlDroppedTotal, previous.FlowControlDropped);
        Interlocked.Exchange(ref _tcpConnectionClosedTotal, previous.TcpConnectionClosed);
        Interlocked.Exchange(ref _tcpConnectionIdleTimeoutTotal, previous.TcpConnectionIdleTimeout);
        Interlocked.Exchange(ref _tcpMessageDiscardedOversizedTotal, previous.TcpMessageOversizedDiscarded);
        Interlocked.Exchange(ref _tcpConnectionResyncLimitExceededTotal, previous.TcpConnectionResyncLimitExceeded);
        Interlocked.Exchange(ref _tcpConnectionFramingTimeoutTotal, previous.TcpConnectionFramingTimeout);
        Interlocked.Exchange(ref _spoolCorruptTailDiscardedTotal, previous.SpoolCorruptTailDiscardedBytes);
    }

    /// <summary>
    /// メタデータ領域（§4.3）へ永続化するための、現在のカウンタ累積値のスナップショットを返す
    /// （<see cref="SeedCumulativeCounters"/> で引き継いだ前回分 + 今回プロセスで加算された分）。
    /// </summary>
    public IngestionCounterSnapshot SnapshotCumulativeCounters() => new(
        InternalBufferDropped: Interlocked.Read(ref _internalBufferDroppedTotal),
        TcpConnectionRejected: Interlocked.Read(ref _tcpConnectionRejectedTotal),
        SpoolEvacuated: Interlocked.Read(ref _spoolEvacuatedTotal),
        SpoolWriteFailed: Interlocked.Read(ref _spoolWriteFailedTotal),
        SpoolDiscarded: Interlocked.Read(ref _spoolDiscardedTotal),
        PersistenceFailed: Interlocked.Read(ref _persistenceFailedTotal),
        FlowControlDropped: Interlocked.Read(ref _flowControlDroppedTotal),
        TcpConnectionClosed: Interlocked.Read(ref _tcpConnectionClosedTotal),
        TcpConnectionIdleTimeout: Interlocked.Read(ref _tcpConnectionIdleTimeoutTotal),
        TcpMessageOversizedDiscarded: Interlocked.Read(ref _tcpMessageDiscardedOversizedTotal),
        TcpConnectionResyncLimitExceeded: Interlocked.Read(ref _tcpConnectionResyncLimitExceededTotal),
        TcpConnectionFramingTimeout: Interlocked.Read(ref _tcpConnectionFramingTimeoutTotal),
        SpoolCorruptTailDiscardedBytes: Interlocked.Read(ref _spoolCorruptTailDiscardedTotal));

    /// <summary>
    /// 内部バッファ破棄カウンタの計器そのもの。テストで
    /// <c>Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector&lt;long&gt;</c>
    /// に直接束縛するために公開する（Meter 名・計器名の文字列一致より頑健なため）。
    /// </summary>
    public Counter<long> InternalBufferDroppedCounter => _internalBufferDropped;

    /// <summary>
    /// TCP 接続拒否カウンタの計器そのもの（テスト用。<see cref="InternalBufferDroppedCounter"/> と同じ理由）。
    /// </summary>
    public Counter<long> TcpConnectionRejectedCounter => _tcpConnectionRejected;

    /// <summary>スプール退避カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> SpoolEvacuatedCounter => _spoolEvacuated;

    /// <summary>スプール書込失敗カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> SpoolWriteFailedCounter => _spoolWriteFailed;

    /// <summary>スプール破棄カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> SpoolDiscardedCounter => _spoolDiscarded;

    /// <summary>永続化失敗カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> PersistenceFailedCounter => _persistenceFailed;

    /// <summary>流量制御破棄カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> FlowControlDroppedCounter => _flowControlDropped;

    /// <summary>UDP 受信エラーカウンタの計器そのもの（テスト用。<see cref="InternalBufferDroppedCounter"/> と同じ理由。Issue #142）。</summary>
    public Counter<long> UdpReceiveErrorCounter => _udpReceiveError;

    /// <summary>TCP 接続断カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> TcpConnectionClosedCounter => _tcpConnectionClosed;

    /// <summary>TCP 接続アイドルタイムアウトカウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> TcpConnectionIdleTimeoutCounter => _tcpConnectionIdleTimeout;

    /// <summary>TCP メッセージ破棄（上限超過）カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> TcpMessageDiscardedOversizedCounter => _tcpMessageDiscardedOversized;

    /// <summary>TCP 接続再同期上限カウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> TcpConnectionResyncLimitExceededCounter => _tcpConnectionResyncLimitExceeded;

    /// <summary>TCP フレーミング進捗タイムアウトカウンタの計器そのもの（テスト用）。</summary>
    public Counter<long> TcpConnectionFramingTimeoutCounter => _tcpConnectionFramingTimeout;

    /// <summary>スプール末尾破損破棄カウンタの計器そのもの（テスト用。Issue #201）。</summary>
    public Counter<long> SpoolCorruptTailDiscardedCounter => _spoolCorruptTailDiscarded;

    /// <summary>TLS ハンドシェイク失敗カウンタの計器そのもの（テスト用。Issue #137）。</summary>
    public Counter<long> TlsHandshakeFailureCounter => _tlsHandshakeFailure;

    /// <summary>解析失敗（保存済み）カウンタの計器そのもの（テスト用。Issue #270）。</summary>
    public Counter<long> ParseFailedSavedCounter => _parseFailedSaved;

    /// <summary>TCP 不完全メッセージカウンタの計器そのもの（テスト用。Issue #270）。</summary>
    public Counter<long> TcpIncompleteMessageCounter => _tcpIncompleteMessage;

    public void Dispose() => _meter.Dispose();
}
