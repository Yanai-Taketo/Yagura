using System.Diagnostics.Metrics;
using System.Net.NetworkInformation;

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
/// 失敗」「スプール破棄」「永続化失敗」を追加した。M4-4 で「流量制御破棄」（挿入点のみ。
/// <see cref="Yagura.Ingestion.FlowControl.NoopIngressGate"/> が発火させることはない）と、
/// OS 統計突合ゲージ（§4.2）を追加した。残り（解析失敗（保存済み）・TCP 接続断・TLS
/// ハンドシェイク失敗・TCP 不完全メッセージ）は該当機能（TLS 受信等）の実装時に追加する。
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

    private readonly ObservableGauge<long>? _osUdpIPv4DatagramsDiscarded;
    private readonly ObservableGauge<long>? _osUdpIPv6DatagramsDiscarded;
    private long _osUdpIPv4BaselineDiscarded;
    private long _osUdpIPv6BaselineDiscarded;
    private readonly bool _osUdpIPv4StatsAvailable;
    private readonly bool _osUdpIPv6StatsAvailable;

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

        // architecture.md §3.1・§4.1「永続化失敗」: リトライとスプール退避でも救えず失われた件数
        // （スプールなし縮退中の喪失を含む。§1.2）。
        _persistenceFailed = _meter.CreateCounter<long>(
            "yagura.ingestion.persistence.failed",
            unit: "{record}",
            description: "リトライ・スプール退避でも救えず失われた件数（スプールなし縮退中の喪失を含む）。");

        // architecture.md §3.1・§4.1「流量制御破棄」（M4-4 で追加）: 流量制御（§3.3）が拒否した
        // 件数。v0.1 は挿入点のみで判定・破棄は行わないため（NoopIngressGate）、この計器は
        // 常に 0 のまま推移する——それでも「発火は必ず計測される」（§3.3）という約束のため
        // 挿入点（IIngressGate.ShouldAdmit の呼び出し元）で計上する枠を今のうちに設ける。
        _flowControlDropped = _meter.CreateCounter<long>(
            "yagura.ingestion.flow_control.dropped",
            unit: "{datagram}",
            description: "流量制御により破棄した件数（v0.1 の NoopIngressGate では常に 0）。");

        // architecture.md §4.2 OS レベル取りこぼしの観測（M4-4 で実機検証: Windows ARM64・
        // SDK 10.0.301 で IPGlobalProperties.GetUdpIPv4Statistics()/GetUdpIPv6Statistics() の
        // IncomingDatagramsDiscarded が実際に取得でき、値が単調増加する生きた値であることを
        // 確認済み（本クラス実装時の実機確認。推測ではない）。システム全体統計であり「本製品の
        // ソケットで落ちた」ことの直接証明ではない粒度の限界は§4.2 のとおり——ObservableGauge の
        // description にもこの限界を明記する。
        //
        // 「プロセス起動時からの差分」として公開する（システム全体の累積値をそのまま出すと、
        // 本製品起動前からの値混入で「本製品稼働中の増分」の解釈を誤らせるため）。
        // GetIPGlobalProperties() 呼び出し自体が稀に PlatformNotSupportedException 等を
        // 投げ得るため、コンストラクタで一度だけ試行し、失敗した場合はゲージを登録しない
        // （枠のみ・取得不可を正直に反映する。§4.2「取得できない…場合は正直に報告して枠のみ」）。
        try
        {
            var v4Baseline = IPGlobalProperties.GetIPGlobalProperties().GetUdpIPv4Statistics();
            _osUdpIPv4BaselineDiscarded = v4Baseline.IncomingDatagramsDiscarded;
            _osUdpIPv4StatsAvailable = true;
        }
        catch (NetworkInformationException)
        {
            _osUdpIPv4StatsAvailable = false;
        }
        catch (PlatformNotSupportedException)
        {
            _osUdpIPv4StatsAvailable = false;
        }

        try
        {
            var v6Baseline = IPGlobalProperties.GetIPGlobalProperties().GetUdpIPv6Statistics();
            _osUdpIPv6BaselineDiscarded = v6Baseline.IncomingDatagramsDiscarded;
            _osUdpIPv6StatsAvailable = true;
        }
        catch (NetworkInformationException)
        {
            _osUdpIPv6StatsAvailable = false;
        }
        catch (PlatformNotSupportedException)
        {
            _osUdpIPv6StatsAvailable = false;
        }

        if (_osUdpIPv4StatsAvailable)
        {
            _osUdpIPv4DatagramsDiscarded = _meter.CreateObservableGauge(
                "yagura.os.udp.ipv4.datagrams_discarded",
                ObserveOsUdpIPv4DatagramsDiscarded,
                unit: "{datagram}",
                description:
                    "OS の IPv4 UDP 統計 (IncomingDatagramsDiscarded) のプロセス起動時からの増分（§4.2 突合値）。" +
                    "システム全体の値であり、本製品のソケット単独の破棄数ではない（粒度の限界。§4.2）。");
        }

        if (_osUdpIPv6StatsAvailable)
        {
            _osUdpIPv6DatagramsDiscarded = _meter.CreateObservableGauge(
                "yagura.os.udp.ipv6.datagrams_discarded",
                ObserveOsUdpIPv6DatagramsDiscarded,
                unit: "{datagram}",
                description:
                    "OS の IPv6 UDP 統計 (IncomingDatagramsDiscarded) のプロセス起動時からの増分（§4.2 突合値）。" +
                    "システム全体の値であり、本製品のソケット単独の破棄数ではない（粒度の限界。§4.2）。");
        }
    }

    private long ObserveOsUdpIPv4DatagramsDiscarded()
    {
        var current = IPGlobalProperties.GetIPGlobalProperties().GetUdpIPv4Statistics().IncomingDatagramsDiscarded;
        return Math.Max(0, current - _osUdpIPv4BaselineDiscarded);
    }

    private long ObserveOsUdpIPv6DatagramsDiscarded()
    {
        var current = IPGlobalProperties.GetIPGlobalProperties().GetUdpIPv6Statistics().IncomingDatagramsDiscarded;
        return Math.Max(0, current - _osUdpIPv6BaselineDiscarded);
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
    /// スプールへの退避を 1 件計上する（損失ではない。§4.1）。
    /// </summary>
    public void RecordSpoolEvacuated()
    {
        _spoolEvacuated.Add(1);
        Interlocked.Increment(ref _spoolEvacuatedTotal);
    }

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
    /// リトライ・スプール退避でも救えなかった永続化失敗を 1 件計上する
    /// （スプールなし縮退中の喪失を含む。§1.2・§4.1）。
    /// </summary>
    public void RecordPersistenceFailed()
    {
        _persistenceFailed.Add(1);
        Interlocked.Increment(ref _persistenceFailedTotal);
    }

    /// <summary>
    /// 流量制御による破棄を 1 件計上する（§3.3・§4.1。v0.1 の NoopIngressGate では発火しない）。
    /// </summary>
    public void RecordFlowControlDropped()
    {
        _flowControlDropped.Add(1);
        Interlocked.Increment(ref _flowControlDroppedTotal);
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
        FlowControlDropped: Interlocked.Read(ref _flowControlDroppedTotal));

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

    /// <summary>OS レベル IPv4 UDP 破棄ゲージが利用可能か（実機検証。§4.2）。</summary>
    public bool OsUdpIPv4StatsAvailable => _osUdpIPv4StatsAvailable;

    /// <summary>OS レベル IPv6 UDP 破棄ゲージが利用可能か（実機検証。§4.2）。</summary>
    public bool OsUdpIPv6StatsAvailable => _osUdpIPv6StatsAvailable;

    /// <summary>
    /// OS レベル IPv4 UDP 破棄ゲージの計器そのもの（テスト用。<see cref="OsUdpIPv4StatsAvailable"/>
    /// が <c>false</c> の場合は <c>null</c>）。<see cref="InternalBufferDroppedCounter"/> と
    /// 同じ理由で、Meter 名・計器名の文字列一致より頑健な直接束縛を可能にする。
    /// </summary>
    public ObservableGauge<long>? OsUdpIPv4DatagramsDiscardedGauge => _osUdpIPv4DatagramsDiscarded;

    /// <summary>OS レベル IPv6 UDP 破棄ゲージの計器そのもの（テスト用。同上）。</summary>
    public ObservableGauge<long>? OsUdpIPv6DatagramsDiscardedGauge => _osUdpIPv6DatagramsDiscarded;

    public void Dispose() => _meter.Dispose();
}
