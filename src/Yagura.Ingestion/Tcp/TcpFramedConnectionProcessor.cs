using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.FlowControl;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Tcp;

/// <summary>
/// 1 接続分の「読み取り → <see cref="TcpFrameDecoder"/> によるフレーム境界確定 → Q1 投入」
/// ループを、具体的な <see cref="Stream"/> の種類（平文の <see cref="System.Net.Sockets.NetworkStream"/>
/// か、TLS の <see cref="System.Net.Security.SslStream"/> か）に依存せず実行する共通処理
/// （Issue #137）。
/// </summary>
/// <remarks>
/// <para>
/// <b>抽出の経緯</b>: <see cref="TcpSyslogListener.HandleConnectionAsync"/>（M4-1・Issue #140・#143・
/// PR #169 のオーナー決定 2026-07-09 を経て確立した、アイドルタイムアウト・再同期バイト数上限・
/// フレーミング進捗タイムアウトの 3 天井を持つ読み取りループ）は、ソケット取得後は一貫して
/// <see cref="Stream"/> の抽象メンバー（<c>ReadAsync</c>）のみに依存しており、<c>NetworkStream</c>
/// 固有の API を使っていなかった。TLS 受信リスナ（<see cref="Yagura.Ingestion.Tls.TlsSyslogListener"/>）
/// は「既存の TCP 受信パイプラインの上に <c>SslStream</c> を挟む」設計（security.md §6・Issue #137
/// 依頼）のため、このループをそのまま共有できる——複製せず本クラスへ抽出し、両リスナから呼ぶ。
/// </para>
/// <para>
/// 抽出されなかった部分（Accept ループ・同時接続数上限・bind/dual-stack・TLS ハンドシェイクの実行）は
/// リスナごとに異なるため、各リスナ（<see cref="TcpSyslogListener"/> / <c>TlsSyslogListener</c>）が
/// 個別に持つ。
/// </para>
/// </remarks>
internal sealed class TcpFramedConnectionProcessor
{
    private readonly ChannelWriter<RawDatagram> _q1Writer;
    private readonly IIngressGate _ingressGate;
    private readonly IngestionMetrics _metrics;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;

    public TcpFramedConnectionProcessor(
        ChannelWriter<RawDatagram> q1Writer,
        IIngressGate ingressGate,
        IngestionMetrics metrics,
        ILogger? logger,
        TimeProvider timeProvider)
    {
        _q1Writer = q1Writer;
        _ingressGate = ingressGate;
        _metrics = metrics;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>1 接続分の読み取りループの結果（呼び出し元がカウンタ計上・ログ出力を仕分けるための内訳）。</summary>
    public readonly struct Outcome
    {
        public required bool IdleTimedOut { get; init; }

        public required bool ResyncLimitExceeded { get; init; }

        public required bool FramingProgressTimedOut { get; init; }
    }

    /// <summary>
    /// 接続 1 本分の読み取りループを実行する。呼び出し元は接続の Accept・同時接続数の管理・
    /// （TLS の場合）ハンドシェイクの実行までを済ませ、以降のアプリケーションデータの読み取りに
    /// 使う <paramref name="stream"/> を渡す。ループを抜けたら（正常終了・異常終了いずれも）
    /// <see cref="Outcome"/> を返す——接続断カウンタ・ログの計上は呼び出し元の責務のままとする
    /// （<see cref="TcpSyslogListener.HandleConnectionAsync"/> と同じ責務分担）。
    /// </summary>
    /// <param name="stream">読み取り対象（平文は <c>NetworkStream</c>、TLS は認証済みの <c>SslStream</c>）。</param>
    /// <param name="protocol">Q1 へ投入する <see cref="RawDatagram.Protocol"/> の値。</param>
    /// <param name="sourceAddress">送信元アドレスの文字列表現（正規化済み）。</param>
    /// <param name="sourcePort">送信元ポート。</param>
    /// <param name="remoteAddress">流量制御挿入点（<see cref="IIngressGate.ShouldAdmit"/>）へ渡す送信元 <see cref="IPAddress"/>。</param>
    /// <param name="decoderOptions">フレーミングの構成（1 メッセージ上限・再同期上限・octet-counting 強制の有無）。</param>
    /// <param name="idleTimeout">アイドルタイムアウト（<see cref="TimeSpan.Zero"/> 以下で無効化）。</param>
    /// <param name="framingProgressTimeout">フレーミング進捗タイムアウト（同上）。</param>
    /// <param name="connectionLabel">ログ文言に使う接続種別ラベル（例: <c>"TCP"</c>・<c>"TLS"</c>）。</param>
    /// <param name="stoppingToken">リスナ停止のトークン。</param>
    public async Task<Outcome> RunAsync(
        Stream stream,
        Protocol protocol,
        string sourceAddress,
        int sourcePort,
        IPAddress? remoteAddress,
        TcpFrameDecoderOptions decoderOptions,
        TimeSpan idleTimeout,
        TimeSpan framingProgressTimeout,
        string connectionLabel,
        CancellationToken stoppingToken)
    {
        var decoder = new TcpFrameDecoder(decoderOptions);
        var buffer = new byte[8192];
        var previousOversizedDiscardedCount = 0;
        var idleTimedOut = false;
        var resyncLimitExceeded = false;
        var framingProgressTimedOut = false;

        var framingProgressEnabled = framingProgressTimeout > TimeSpan.Zero;
        DateTimeOffset? lastFramingProgressAt = null;

        var idleTimeoutEnabled = idleTimeout > TimeSpan.Zero;
        CancellationTokenSource? idleCts = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                CancellationToken readToken;
                if (idleTimeoutEnabled)
                {
                    idleCts = TcpSyslogListener.RenewIdleReadCancellation(idleCts, stoppingToken, idleTimeout);
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
                    idleTimedOut = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    // 相手の異常切断（RST 等）。TLS の場合は復号エラー・アラートも IOException として
                    // 表面化する（SslStream の一般的な挙動）。接続断として扱い、読みかけデータを
                    // Incomplete で流す。
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (bytesRead == 0)
                {
                    // 相手が正常にシャットダウン（FIN。TLS は close_notify）した。接続断。
                    break;
                }

                var receivedAt = _timeProvider.GetUtcNow();
                lastFramingProgressAt ??= receivedAt;

                IReadOnlyList<byte[]> messages;
                var unrecoverableViolation = false;
                try
                {
                    messages = decoder.Push(buffer.AsSpan(0, bytesRead));
                }
                catch (TcpFrameSizeExceededException ex)
                {
                    if (ex.Kind == TcpFrameViolationKind.ResyncByteLimitExceeded)
                    {
                        resyncLimitExceeded = true;
                        _logger?.LogWarning(
                            ex,
                            "{ConnectionLabel} 接続 {SourceAddress}:{SourcePort} で有効なメッセージが確定しないまま読み捨てたバイト数が " +
                            "上限 {MaxResyncBytes} を超えたため切断します。",
                            connectionLabel,
                            sourceAddress,
                            sourcePort,
                            decoderOptions.MaxResyncBytes);
                    }
                    else
                    {
                        _logger?.LogWarning(
                            ex,
                            "{ConnectionLabel} 接続 {SourceAddress}:{SourcePort} で再同期不能なフレーム破損を検出したため切断します。",
                            connectionLabel,
                            sourceAddress,
                            sourcePort);
                    }

                    messages = ex.CompletedMessages;
                    unrecoverableViolation = true;
                }

                if (decoder.OversizedMessagesDiscardedCount != previousOversizedDiscardedCount)
                {
                    var discardedThisPush = decoder.OversizedMessagesDiscardedCount - previousOversizedDiscardedCount;
                    previousOversizedDiscardedCount = decoder.OversizedMessagesDiscardedCount;

                    for (var i = 0; i < discardedThisPush; i++)
                    {
                        _metrics.RecordTcpMessageDiscardedOversized();
                    }

                    _logger?.LogWarning(
                        "{ConnectionLabel} 接続 {SourceAddress}:{SourcePort} で 1 メッセージのサイズ上限超過により " +
                        "{Count} 件を破棄しました（接続は継続します）。",
                        connectionLabel,
                        sourceAddress,
                        sourcePort,
                        discardedThisPush);
                }

                foreach (var message in messages)
                {
                    if (!_ingressGate.ShouldAdmit(remoteAddress ?? IPAddress.None, message))
                    {
                        _metrics.RecordFlowControlDropped();
                        continue;
                    }

                    var datagram = new RawDatagram(
                        ReceivedAt: receivedAt,
                        SourceAddress: sourceAddress,
                        SourcePort: sourcePort,
                        Protocol: protocol,
                        Payload: message);

                    await _q1Writer.WriteAsync(datagram, CancellationToken.None).ConfigureAwait(false);
                }

                if (unrecoverableViolation)
                {
                    break;
                }

                if (messages.Count > 0)
                {
                    lastFramingProgressAt = _timeProvider.GetUtcNow();
                }
                else if (framingProgressEnabled
                    && _timeProvider.GetUtcNow() - lastFramingProgressAt!.Value > framingProgressTimeout)
                {
                    framingProgressTimedOut = true;
                    _logger?.LogWarning(
                        "{ConnectionLabel} 接続 {SourceAddress}:{SourcePort} で有効なメッセージが確定しないまま " +
                        "{FramingProgressTimeout} が経過したため切断します（フレーミング進捗タイムアウト）。",
                        connectionLabel,
                        sourceAddress,
                        sourcePort,
                        framingProgressTimeout);
                    break;
                }
            }

            var incomplete = decoder.Flush();
            if (incomplete is not null)
            {
                var datagram = new RawDatagram(
                    ReceivedAt: _timeProvider.GetUtcNow(),
                    SourceAddress: sourceAddress,
                    SourcePort: sourcePort,
                    Protocol: protocol,
                    Payload: incomplete,
                    Incomplete: true);

                await _q1Writer.WriteAsync(datagram, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            idleCts?.Dispose();
        }

        return new Outcome
        {
            IdleTimedOut = idleTimedOut,
            ResyncLimitExceeded = resyncLimitExceeded,
            FramingProgressTimedOut = framingProgressTimedOut,
        };
    }
}
