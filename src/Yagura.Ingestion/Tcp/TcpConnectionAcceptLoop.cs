using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.Net;

namespace Yagura.Ingestion.Tcp;

/// <summary>
/// TCP Accept ループ・同時接続数上限判定・接続タスクの追跡と待機を、平文 TCP
/// （<see cref="TcpSyslogListener"/>）と TLS（<see cref="Yagura.Ingestion.Tls.TlsSyslogListener"/>）で
/// 共有する共通処理（Phase 5「残存重複の解消」）。
/// </summary>
/// <remarks>
/// <para>
/// <b>抽出範囲</b>: bind（dual-stack 判定を含む）・Accept ループ・同時接続数上限判定と拒否処理
/// （<see cref="IngestionMetrics.RecordTcpConnectionRejected"/>）・接続タスクの追跡（<see cref="StopAsync"/>
/// での一括待機）は、両リスナの実装が 1 行も違わなかったため本クラスへ抽出した。Accept 後の
/// 1 接続をどう処理するか（読み取りループの実行・TLS ハンドシェイク・<c>Outcome</c> に応じた
/// メトリクス/ログの出し分け）はリスナごとに異なるため、コンストラクタ引数
/// <c>connectionHandler</c> としてリスナから注入してもらう——本クラスは Accept した
/// <see cref="TcpClient"/> を渡して呼ぶだけで、中身の処理内容には関与しない。
/// </para>
/// <para>
/// <b>抽出しなかった部分</b>: 接続 1 本の処理完了後に行う同時接続数のデクリメントと
/// <see cref="IngestionMetrics.RecordTcpConnectionClosed"/> の計上は、両リスナで意味は同一だが
/// 実行タイミング（<c>Outcome</c> 分岐との前後関係）が異なるため、各リスナの
/// <c>HandleConnectionAsync</c> に残したままにしている（並行処理の意味を 1 ビットも変えないため）。
/// デクリメントだけは <see cref="ReleaseConnectionSlot"/> 経由で本クラスへ通知してもらう
/// （カウンタの実体は本クラスが保持するため）。<c>Outcome</c> 分岐そのもの・ログ文言（"TCP"/"TLS"
/// で異なる）も両リスナ固有のまま残す。
/// </para>
/// </remarks>
internal sealed class TcpConnectionAcceptLoop
{
    private readonly string _listenerName;
    private readonly int _maxConcurrentConnections;
    private readonly IngestionMetrics _metrics;
    private readonly ILogger? _logger;
    private readonly Func<TcpClient, CancellationToken, Task> _connectionHandler;

    private TcpListener? _tcpListener;
    private Task? _acceptLoopTask;
    private CancellationTokenSource? _stoppingCts;

    // 現在接続中のソケットの処理タスク一式。StopAsync でまとめて待つ。
    private readonly ConcurrentDictionary<Task, byte> _connectionTasks = new();

    // 同時接続数の有限化（architecture.md §3.1）。Accept ごとにインクリメントし、
    // 接続終了時に呼び出し元（各リスナ）が ReleaseConnectionSlot でデクリメントする。
    private int _currentConnectionCount;

    public TcpConnectionAcceptLoop(
        string listenerName,
        int maxConcurrentConnections,
        IngestionMetrics metrics,
        ILogger? logger,
        Func<TcpClient, CancellationToken, Task> connectionHandler)
    {
        ArgumentException.ThrowIfNullOrEmpty(listenerName);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(connectionHandler);

        _listenerName = listenerName;
        _maxConcurrentConnections = maxConcurrentConnections;
        _metrics = metrics;
        _logger = logger;
        _connectionHandler = connectionHandler;
    }

    /// <summary>
    /// 実際に束縛された TCP ポート。テスト用の OS 採番（ポート 0 指定）時に、開始後の実ポートを
    /// 取得するために使う。
    /// </summary>
    public int BoundPort { get; private set; }

    /// <summary>現在の接続数（テスト・観測用）。</summary>
    public int CurrentConnectionCount => Volatile.Read(ref _currentConnectionCount);

    /// <summary>
    /// ソケットを bind し、Accept ループを開始する（architecture.md §1.2「受信を最初に開く」）。
    /// </summary>
    public Task StartAsync(string bindAddress, int port, bool bindAddressIsExplicit)
    {
        if (_tcpListener is not null)
        {
            throw new InvalidOperationException($"{_listenerName} は既に開始されている。");
        }

        var parsedBindAddress = IPAddress.Parse(bindAddress);
        if (DualStackBindAddress.IsIPv6Wildcard(parsedBindAddress))
        {
            _tcpListener = DualStackTcpListenerFactory.CreateOrFallBack(port, bindAddressIsExplicit, _logger);
        }
        else
        {
            // 明示的な 0.0.0.0（IPv4 のみ）・特定の IPv4/IPv6 アドレス指定は、
            // 従来どおりそのアドレスファミリ単独のソケットで bind する。
            _tcpListener = new TcpListener(parsedBindAddress, port);
            _tcpListener.Start();
        }

        BoundPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;

        _stoppingCts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_stoppingCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Accept ループ・全接続の処理タスクを止め、ソケットを閉じる（architecture.md §1.3 手順 1）。
    /// </summary>
    public async Task StopAsync()
    {
        if (_tcpListener is null)
        {
            return;
        }

        // キャンセル → ループ終了待ち → 破棄、の順序を踏む。
        _stoppingCts?.Cancel();

        if (_acceptLoopTask is not null)
        {
            await _acceptLoopTask.ConfigureAwait(false);
        }

        _tcpListener.Stop();

        // 停止順序（依頼「リスナ停止 → 接続クローズ → drain」）: リスナは上で既に停止済み。
        // 個々の接続処理は stoppingToken のキャンセルを受けて自ら終了する。
        var pending = _connectionTasks.Keys.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        _stoppingCts?.Dispose();
        _stoppingCts = null;
        _tcpListener = null;
    }

    /// <summary>
    /// 接続 1 本の処理完了後、リスナが同時接続数の枠を返すために呼ぶ。カウンタの実体は本クラスが
    /// 保持するため、デクリメント自体は呼び出し元（各リスナの <c>HandleConnectionAsync</c>）から
    /// 委譲してもらう——実行タイミング（<c>Outcome</c> 分岐との前後関係）はリスナごとに異なり、
    /// 本クラスからは制御しない。
    /// </summary>
    public void ReleaseConnectionSlot() => Interlocked.Decrement(ref _currentConnectionCount);

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
            if (Interlocked.Increment(ref _currentConnectionCount) > _maxConcurrentConnections)
            {
                Interlocked.Decrement(ref _currentConnectionCount);
                _metrics.RecordTcpConnectionRejected();
                client.Close();
                client.Dispose();
                continue;
            }

            var connectionTask = Task.Run(() => _connectionHandler(client, stoppingToken), CancellationToken.None);
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
}
