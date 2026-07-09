using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Net;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tcp;

/// <summary>
/// TCP の受信段（architecture.md §2.1・§3.1・§4.5。M4-1）。
/// </summary>
/// <remarks>
/// <para>
/// UDP（<see cref="Yagura.Ingestion.Udp.UdpSyslogListener"/>）と構造を揃える: 読み取りは
/// 解析・書き込みを行わず、境界が確定したメッセージを <see cref="RawDatagram"/> へ封筒化して
/// Q1 へ投入するだけである。
/// </para>
/// <para>
/// <b>Q1 満杯時の挙動が UDP と異なる</b>（architecture.md §3.1）: UDP は破棄するが、TCP は
/// **読み取りを停止する**。本実装は Q1 への投入を <c>ChannelWriter.WriteAsync</c>（await）で
/// 行うことでこれを実現する——Q1 が満杯なら WriteAsync が完了せず、結果として当該接続の
/// ソケット読み取りが自然に停滞し、OS の TCP フロー制御（受信ウィンドウの縮小）を通じて
/// 送信側へ伝搬する。停止中であることの可視化（ゲージ）は M4-4 で追加する。
/// </para>
/// <para>
/// <b>同時接続数上限</b>（§3.1・M-14）: 上限到達時は Accept 直後に接続を閉じ（新規拒否）、
/// <see cref="IngestionMetrics.RecordTcpConnectionRejected"/> で計上する。既存接続の読み取り
/// 停止中に接続数が無限に積み上がることを防ぐ有限化である。
/// </para>
/// <para>
/// <b>アイドルタイムアウト</b>（§4.5・<see cref="TcpSyslogListenerOptions.IdleTimeout"/>。
/// Issue #140）: 同時接続数上限は「読み取り停止中も接続を保持する」設計の帰結として必要な
/// 有限化だが、それ自体が無言・低速な接続（何も送らない、または 1 バイトだけ送って黙る）に
/// 占有され続けると、正常な送信元の新規接続が拒否され続ける（slowloris 型の資源枯渇）。
/// 本実装は最後にバイトを読み取ってから <see cref="TcpSyslogListenerOptions.IdleTimeout"/> が
/// 経過した接続を切断し、枠を返す。切断は
/// <see cref="IngestionMetrics.RecordTcpConnectionIdleTimeout"/> で計上する。
/// </para>
/// <para>
/// <b>1 メッセージの逸脱への耐性</b>（§4.5。Issue #143）: octet-counting のフレーム間に紛れた
/// LF/CR は <see cref="TcpFrameDecoder"/> が寛容にスキップして再同期し、1 メッセージのサイズ
/// 上限超過（octet-counting・non-transparent-framing とも）は当該メッセージのみを破棄して
/// 接続を維持する（<see cref="TcpFrameDecoder.OversizedMessagesDiscardedCount"/> の差分を
/// <see cref="IngestionMetrics.RecordTcpMessageDiscardedOversized"/> で計上）。回復不能な
/// フレーミング違反（<see cref="TcpFrameSizeExceededException"/>）のときのみ接続を切断する。
/// </para>
/// <para>
/// <b>寛容化の天井</b>（§4.5。PR #169 レビュー指摘 3 へのオーナー決定 2026-07-09）: 上記の
/// 寛容化は、業界主流（rsyslog・syslog-ng・Fluent Bit 等のフレーミングエラー即切断）が持つ
/// 一次防御を外した状態になるため、2 つの天井との組で同等の防御水準を保つ——
/// ①<b>再同期バイト数上限</b>（<see cref="TcpSyslogListenerOptions.MaxResyncBytes"/>。有効な
/// メッセージが 1 件も確定しないまま読み捨てたバイト数の上限。判定は
/// <see cref="TcpFrameDecoder"/> が行い、超過は
/// <see cref="IngestionMetrics.RecordTcpConnectionResyncLimitExceeded"/> で計上）、
/// ②<b>フレーミング進捗タイムアウト</b>（<see cref="TcpSyslogListenerOptions.FramingProgressTimeout"/>。
/// バイトは届いているのに有効なメッセージが確定しないまま経過した時間の上限。読み取りが
/// 起き続けるためアイドルタイムアウトでは回収できない低速トリクルを回収する。超過は
/// <see cref="IngestionMetrics.RecordTcpConnectionFramingTimeout"/> で計上）。いずれも有効な
/// メッセージが 1 件確定するたびにリセットされ、正常な送信元は巻き込まれない。
/// </para>
/// <para>
/// <b>切断時の不完全メッセージ</b>（database.md §2.1）: 接続が切断された時点で
/// <see cref="TcpFrameDecoder"/> に読みかけのデータが残っていれば、それを
/// <see cref="RawDatagram.Incomplete"/> = <c>true</c> として Q1 へ流す（捨てない）。
/// </para>
/// <para>
/// <b>TCP 接続断の計上</b>（§4.5。Issue #140）: 理由を問わず、接続が終了するたびに
/// <see cref="IngestionMetrics.RecordTcpConnectionClosed"/> を 1 件計上する（損失ではなく
/// 解釈の手がかり。アイドルタイムアウト由来の切断はこれに加えて専用カウンタも計上する）。
/// </para>
/// <para>
/// <b>IPv4/IPv6 デュアルスタック受信（Issue #133）</b>: <see cref="TcpSyslogListenerOptions.BindAddress"/>
/// が IPv6 ワイルドカード（<c>::</c>。既定値）のときは、<see cref="Socket.DualMode"/> を有効にした
/// 単一ソケットで bind し、IPv4・IPv6 双方からの接続を受け付ける（<see cref="DualStackBindAddress"/>
/// 参照）。DualMode ソケットが受ける IPv4 由来の接続は <c>RemoteEndPoint.Address</c> が
/// IPv4-mapped IPv6（<c>::ffff:x.x.x.x</c>）として現れるため、<see cref="RawDatagram.SourceAddress"/>
/// へ書き込む前に <see cref="DualStackBindAddress.NormalizeSourceAddress"/> で純粋な IPv4 表現へ
/// 正規化する（UDP 側・ADR-0007 決定 2 と同じ規約）。
/// </para>
/// </remarks>
public sealed class TcpSyslogListener : IAsyncDisposable
{
    private readonly TcpSyslogListenerOptions _options;
    private readonly ChannelWriter<RawDatagram> _q1Writer;
    private readonly IIngressGate _ingressGate;
    private readonly IngestionMetrics _metrics;
    private readonly ILogger<TcpSyslogListener>? _logger;

    private TcpListener? _tcpListener;
    private Task? _acceptLoopTask;
    private CancellationTokenSource? _stoppingCts;

    // 現在接続中のソケットの読み取りループタスク一式。StopAsync でまとめて待つ。
    private readonly ConcurrentDictionary<Task, byte> _connectionTasks = new();

    // 同時接続数の有限化（§3.1）。Accept ごとにインクリメントし、接続終了時にデクリメントする。
    private int _currentConnectionCount;

    public TcpSyslogListener(
        TcpSyslogListenerOptions options,
        ChannelWriter<RawDatagram> q1Writer,
        IIngressGate ingressGate,
        IngestionMetrics metrics,
        ILogger<TcpSyslogListener>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(q1Writer);
        ArgumentNullException.ThrowIfNull(ingressGate);
        ArgumentNullException.ThrowIfNull(metrics);

        _options = options;
        _q1Writer = q1Writer;
        _ingressGate = ingressGate;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// 実際に束縛された TCP ポート。<see cref="TcpSyslogListenerOptions.Port"/> に 0
    /// を指定した場合（テスト用の OS 採番）に、開始後の実ポートを取得するために使う。
    /// </summary>
    public int BoundPort { get; private set; }

    /// <summary>
    /// 現在の接続数（テスト・観測用）。
    /// </summary>
    public int CurrentConnectionCount => Volatile.Read(ref _currentConnectionCount);

    /// <summary>
    /// ソケットを bind し、Accept ループを開始する（architecture.md §1.2「受信を最初に開く」）。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_tcpListener is not null)
        {
            throw new InvalidOperationException("TcpSyslogListener は既に開始されている。");
        }

        var bindAddress = IPAddress.Parse(_options.BindAddress);
        _tcpListener = new TcpListener(bindAddress, _options.Port);
        if (DualStackBindAddress.IsIPv6Wildcard(bindAddress))
        {
            // DualMode ソケット（Issue #133）。TcpListener.Server は Start() 前であれば
            // 直接設定できる（.NET の確立されたパターン——Kestrel 側の同種の扱いは
            // Yagura.Host.ListenerBindPlan の remarks 参照）。
            _tcpListener.Server.DualMode = true;
        }

        _tcpListener.Start();
        BoundPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;

        _stoppingCts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_stoppingCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Accept ループ・全接続の読み取りループを止め、ソケットを閉じる（architecture.md §1.3 手順 1）。
    /// </summary>
    public async Task StopAsync()
    {
        if (_tcpListener is null)
        {
            return;
        }

        // UdpSyslogListener と同じ順序（キャンセル → ループ終了待ち → 破棄）を踏む。
        _stoppingCts?.Cancel();

        if (_acceptLoopTask is not null)
        {
            await _acceptLoopTask.ConfigureAwait(false);
        }

        _tcpListener.Stop();

        // 停止順序（依頼「リスナ停止 → 接続クローズ → drain」）: リスナは上で既に停止済み。
        // 個々の接続の読み取りループは stoppingToken のキャンセルを受けて自ら終了し、
        // 切断時に Incomplete マークを付けて Q1 へ流してから完了する（下記 HandleConnectionAsync）。
        var pending = _connectionTasks.Keys.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        _stoppingCts?.Dispose();
        _stoppingCts = null;
        _tcpListener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken stoppingToken)
    {
        var listener = _tcpListener!;

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // 個々の Accept 失敗でループを止めない（UDP の個別データグラムエラーと同じ方針）。
                continue;
            }

            // 同時接続数上限（architecture.md §3.1・M-14）: 上限到達時は Accept 後即クローズし、
            // 新規接続を拒否する（既存接続を守る）。
            if (Interlocked.Increment(ref _currentConnectionCount) > _options.MaxConcurrentConnections)
            {
                Interlocked.Decrement(ref _currentConnectionCount);
                _metrics.RecordTcpConnectionRejected();
                client.Close();
                client.Dispose();
                continue;
            }

            var connectionTask = Task.Run(() => HandleConnectionAsync(client, stoppingToken), CancellationToken.None);
            _connectionTasks[connectionTask] = 0;

            // 接続終了時に一覧から取り除く（無限に積み上がらないようにする）。継続はバックグラウンドで行い、
            // Accept ループ自体はブロックしない。
            _ = connectionTask.ContinueWith(
                t => _connectionTasks.TryRemove(t, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        // DualMode ソケットが受けた IPv4 接続は ::ffff:x.x.x.x として現れるため正規化する
        // （Issue #133。DualStackBindAddress の remarks 参照）。
        var remoteAddress = remoteEndPoint is null ? null : DualStackBindAddress.NormalizeSourceAddress(remoteEndPoint.Address);
        var sourceAddress = remoteAddress?.ToString() ?? "unknown";
        var sourcePort = remoteEndPoint?.Port ?? 0;

        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions
        {
            MaxMessageLength = _options.MaxMessageLength,
            MaxResyncBytes = _options.MaxResyncBytes,
        });
        var buffer = new byte[8192];
        var previousOversizedDiscardedCount = 0;
        var idleTimedOut = false;
        var resyncLimitExceeded = false;
        var framingProgressTimedOut = false;

        // フレーミング進捗タイムアウト（オーナー決定 2026-07-09。PR #169 レビュー指摘 3 の B）:
        // バイトは届き続けているのに有効なメッセージが 1 件も確定しない接続を回収する
        // （低速トリクル対策。アイドルタイムアウトは「読み取りが起きない」接続を回収する別軸）。
        // 基準時刻は「最初のバイトを受信した時刻」で初期化し、有効なメッセージが 1 件確定する
        // たびに取り直す。判定は読み取り完了ごとに行う——読み取りが起きない接続はアイドル
        // タイムアウト側の管轄のため、ここで能動的なタイマーは張らない。
        var framingProgressEnabled = _options.FramingProgressTimeout > TimeSpan.Zero;
        DateTimeOffset? lastFramingProgressAt = null;

        // Issue #140: アイドルタイムアウト。読み取りのたびに stoppingToken にリンクした新しい
        // CTS を生成し、CancelAfter でその読み取り 1 回分のタイマーを張る（PR #169 レビュー
        // 指摘 1 への対応——単一 CTS の CancelAfter 再スケジュール方式は、タイマー発火と
        // 読み取り成功が僅差で競合すると「キャンセル済みソースへの CancelAfter は no-op」という
        // .NET の仕様により再スケジュールが黙って無視され、直前までデータを受信していた活性
        // 接続を次の読み取りで誤ってアイドル扱いする。読み取りごとに CTS を作り直せば、
        // キャンセル済みソースが次の読み取りへ持ち越される経路自体が存在しない）。
        // 副次効果として、Q1 満杯による WriteAsync の停滞時間（§3.1 の意図された読み取り停止）が
        // アイドル時間へ誤算入される経路も閉じる——タイマーが覆うのは ReadAsync の待機だけになる。
        // IdleTimeout が Zero 以下なら無効化し、stoppingToken をそのまま使う（主にテスト用途）。
        var idleTimeoutEnabled = _options.IdleTimeout > TimeSpan.Zero;
        CancellationTokenSource? idleCts = null;

        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    CancellationToken readToken;
                    if (idleTimeoutEnabled)
                    {
                        idleCts = RenewIdleReadCancellation(idleCts, stoppingToken, _options.IdleTimeout);
                        readToken = idleCts.Token;
                    }
                    else
                    {
                        readToken = stoppingToken;
                    }

                    int bytesRead;
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer, readToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (idleTimeoutEnabled && !stoppingToken.IsCancellationRequested)
                    {
                        // アイドルタイムアウトによる打ち切り（stoppingToken 自体はキャンセルされて
                        // いない——この読み取りのために張ったタイマーが発火した）。無通信接続が
                        // 同時接続枠（§3.1・M-14）を占有し続けることを防ぐ有限化（Issue #140）。
                        // readToken はこの読み取り専用に生成されたため、このキャンセルは
                        // 「この ReadAsync の待機が IdleTimeout 続いた」ことを確実に意味する
                        // （過去の読み取りのタイマー発火が持ち越される競合は構造上起きない）。
                        idleTimedOut = true;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        // 相手の異常切断（RST 等）。接続断として扱い、読みかけデータを Incomplete で流す。
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        // 相手が正常にシャットダウン（FIN）した。接続断。
                        break;
                    }

                    var receivedAt = DateTimeOffset.UtcNow;
                    lastFramingProgressAt ??= receivedAt;

                    // PR #169 レビュー指摘 2 への対応: 回復不能なフレーミング違反の例外が出ても、
                    // 同一チャンク内で既に境界が確定していた正常メッセージは失わない——例外の
                    // CompletedMessages から取り出して通常どおり Q1 へ流し、その後に切断する。
                    IReadOnlyList<byte[]> messages;
                    var unrecoverableViolation = false;
                    try
                    {
                        messages = decoder.Push(buffer.AsSpan(0, bytesRead));
                    }
                    catch (TcpFrameSizeExceededException ex)
                    {
                        // 回復不能なフレーミング違反のときのみ、安全側判断として当該接続を
                        // 切断する（Issue #143 で例外の用途を絞った）: ①再同期不能な深刻な破損
                        // （MSG-LEN の桁の途中に数字以外が混入・桁数異常・int 範囲超過）、
                        // ②再同期バイト数上限の超過（オーナー決定 2026-07-09）。1 メッセージの
                        // サイズ上限超過は decoder 内部で当該メッセージを破棄するだけで済み、
                        // この例外には現れない（下の OversizedMessagesDiscardedCount 参照）。
                        // 切断前に、例外送出までに確定していた正常メッセージ
                        // （ex.CompletedMessages）を Q1 へ流し、読みかけデータが残っていれば
                        // 通常の切断経路と同じく Incomplete として Q1 へ流す（decoder の内部
                        // バッファはそのまま残るため、ループ終了後の Flush() が拾う）。
                        if (ex.Kind == TcpFrameViolationKind.ResyncByteLimitExceeded)
                        {
                            resyncLimitExceeded = true;
                            _logger?.LogWarning(
                                ex,
                                "TCP 接続 {SourceAddress}:{SourcePort} で有効なメッセージが確定しないまま読み捨てたバイト数が " +
                                "上限 {MaxResyncBytes} を超えたため切断します。",
                                sourceAddress,
                                sourcePort,
                                _options.MaxResyncBytes);
                        }
                        else
                        {
                            _logger?.LogWarning(
                                ex,
                                "TCP 接続 {SourceAddress}:{SourcePort} で再同期不能なフレーム破損を検出したため切断します。",
                                sourceAddress,
                                sourcePort);
                        }

                        messages = ex.CompletedMessages;
                        unrecoverableViolation = true;
                    }

                    // Issue #143: 1 メッセージのサイズ上限超過による破棄をカウンタ・ログへ計上する
                    // （decoder は計測 API を持たない純粋ロジックのため、呼び出し元が差分を計上する）。
                    if (decoder.OversizedMessagesDiscardedCount != previousOversizedDiscardedCount)
                    {
                        var discardedThisPush = decoder.OversizedMessagesDiscardedCount - previousOversizedDiscardedCount;
                        previousOversizedDiscardedCount = decoder.OversizedMessagesDiscardedCount;

                        for (var i = 0; i < discardedThisPush; i++)
                        {
                            _metrics.RecordTcpMessageDiscardedOversized();
                        }

                        _logger?.LogWarning(
                            "TCP 接続 {SourceAddress}:{SourcePort} で 1 メッセージのサイズ上限超過により " +
                            "{Count} 件を破棄しました（接続は継続します）。",
                            sourceAddress,
                            sourcePort,
                            discardedThisPush);
                    }

                    foreach (var message in messages)
                    {
                        if (!_ingressGate.ShouldAdmit(remoteAddress ?? IPAddress.None, message))
                        {
                            // 挿入点のみ（architecture.md §3.3）。UDP 側と同じく計上の枠を設ける
                            // （M4-4。v0.1 の NoopIngressGate ではこの分岐に到達しない）。
                            _metrics.RecordFlowControlDropped();
                            continue;
                        }

                        var datagram = new RawDatagram(
                            ReceivedAt: receivedAt,
                            SourceAddress: sourceAddress,
                            SourcePort: sourcePort,
                            Protocol: Protocol.Tcp,
                            Payload: message);

                        // architecture.md §3.1: TCP は Q1 満杯時に破棄せず読み取りを停止する。
                        // WriteAsync を await することで、Q1 が満杯の間はこの接続の読み取り
                        // ループ自体が進まなくなり、OS の TCP フロー制御が送信側へ伝搬する。
                        await _q1Writer.WriteAsync(datagram, CancellationToken.None).ConfigureAwait(false);
                    }

                    if (unrecoverableViolation)
                    {
                        // 確定済みメッセージを流し終えてから切断する（Flush() による Incomplete
                        // 退避はループ終了後の共通経路が行う）。
                        break;
                    }

                    if (messages.Count > 0)
                    {
                        // 有効なメッセージが確定した——フレーミング進捗の基準時刻を取り直す。
                        // 取り直しは上の WriteAsync（Q1 満杯時は §3.1 の意図された読み取り停止で
                        // 長時間停滞し得る）の完了後に行う——停滞時間をフレーミング進捗の停滞と
                        // 誤判定して正常な送信元を切断しないため。
                        lastFramingProgressAt = DateTimeOffset.UtcNow;
                    }
                    else if (framingProgressEnabled
                        && DateTimeOffset.UtcNow - lastFramingProgressAt!.Value > _options.FramingProgressTimeout)
                    {
                        // オーナー決定 2026-07-09 の B: バイトは届いているのに有効なメッセージが
                        // 1 件も確定しないまま FramingProgressTimeout が経過した（低速トリクル・
                        // LF を送らないゴミデータ等——読み取りが起き続けるためアイドルタイム
                        // アウトでは回収できない接続）。切断して同時接続枠を返す。
                        framingProgressTimedOut = true;
                        _logger?.LogWarning(
                            "TCP 接続 {SourceAddress}:{SourcePort} で有効なメッセージが確定しないまま " +
                            "{FramingProgressTimeout} が経過したため切断します（フレーミング進捗タイムアウト）。",
                            sourceAddress,
                            sourcePort,
                            _options.FramingProgressTimeout);
                        break;
                    }
                }

                // 切断時（正常シャットダウン・異常切断・停止要求・アイドルタイムアウト・
                // 再同期不能な破損のいずれか）に読みかけの不完全データが残っていれば、
                // Incomplete として Q1 へ流す（database.md §2.1「不完全は解析失敗に優先」——捨てない）。
                var incomplete = decoder.Flush();
                if (incomplete is not null)
                {
                    var datagram = new RawDatagram(
                        ReceivedAt: DateTimeOffset.UtcNow,
                        SourceAddress: sourceAddress,
                        SourcePort: sourcePort,
                        Protocol: Protocol.Tcp,
                        Payload: incomplete,
                        Incomplete: true);

                    // 停止経路でも drain のベストエフォートを揃えるため CancellationToken.None を使う
                    // （UDP 側 ParsingStage の drain 方針と同じ考え方）。
                    await _q1Writer.WriteAsync(datagram, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            idleCts?.Dispose();

            Interlocked.Decrement(ref _currentConnectionCount);

            // architecture.md §4.5「TCP 接続断」（Issue #140）: 理由を問わず接続終了を 1 件計上する。
            _metrics.RecordTcpConnectionClosed();

            if (idleTimedOut)
            {
                _metrics.RecordTcpConnectionIdleTimeout();
                _logger?.LogInformation(
                    "TCP 接続 {SourceAddress}:{SourcePort} をアイドルタイムアウト（{IdleTimeout}）により切断しました。",
                    sourceAddress,
                    sourcePort,
                    _options.IdleTimeout);
            }
            else if (resyncLimitExceeded)
            {
                // オーナー決定 2026-07-09 の A: 再同期バイト数上限超過による切断（内訳計上。
                // ログは検出箇所で出力済み）。
                _metrics.RecordTcpConnectionResyncLimitExceeded();
            }
            else if (framingProgressTimedOut)
            {
                // オーナー決定 2026-07-09 の B: フレーミング進捗タイムアウトによる切断
                // （内訳計上。ログは検出箇所で出力済み）。
                _metrics.RecordTcpConnectionFramingTimeout();
            }
        }
    }

    /// <summary>
    /// 読み取り 1 回分のアイドルタイマー付きキャンセルソースを生成する（Issue #140。PR #169
    /// レビュー指摘 1 への対応）。前回の読み取りで使ったソース（<paramref name="previous"/>）は
    /// ——タイマー発火と読み取り成功が僅差で競合してキャンセル済みになっていたとしても——
    /// ここで必ず破棄され、新しいソースに置き換わる。「キャンセル済みソースへの
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> は no-op」という .NET の仕様に
    /// 起因する、活性接続の誤アイドル判定（stale なキャンセル状態の次回読み取りへの持ち越し）を
    /// 構造的に防ぐ。
    /// </summary>
    /// <param name="previous">前回の読み取りで使ったソース（初回は <c>null</c>）。破棄される。</param>
    /// <param name="stoppingToken">リスナ停止のトークン（生成するソースにリンクする）。</param>
    /// <param name="idleTimeout">アイドルタイムアウト（生成するソースに CancelAfter で張る）。</param>
    /// <returns>この読み取り専用の新しいキャンセルソース（キャンセル未要求の状態で返る）。</returns>
    internal static CancellationTokenSource RenewIdleReadCancellation(
        CancellationTokenSource? previous,
        CancellationToken stoppingToken,
        TimeSpan idleTimeout)
    {
        previous?.Dispose();

        var renewed = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        renewed.CancelAfter(idleTimeout);
        return renewed;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
