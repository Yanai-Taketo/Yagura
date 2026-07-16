using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Ingestion.Diagnostics;
using Yagura.Ingestion.Udp;
using Yagura.Storage;
using Yagura.Storage.Spool;

namespace Yagura.Ingestion.Parsing;

/// <summary>
/// 解析段の消費ループ（architecture.md §2.1）。Q1 から <see cref="RawDatagram"/> を取り出し、
/// <see cref="SyslogParser"/> で解析して Q2 へ投入する。
/// </summary>
/// <remarks>
/// Q2 が満杯のときの解析段 → 永続化段の境界の溢れ時挙動は「新規投入分をスプールへ退避」
/// である（architecture.md §3.1・§3.2.1「容量: Q2 が満杯で新規投入分を受け取れない」）。
/// <see cref="ChannelWriter{T}.TryWrite"/> を使い、Q2 が満杯で即座に書けない場合は
/// ブロックせずスプールへ退避する（M2 時点の暫定挙動——Q2 の <c>WriteAsync</c> を
/// await してバックプレッシャをかける実装——を本 M4-3 で置き換える。PR #28 オーナー
/// 確認事項 1 の解消）。停止時（<see cref="RunAsync"/> の <c>stoppingToken</c> キャンセル後）
/// も同じスプール退避経路を使い、Q1 の残り・処理中の 1 件を DB を待たず退避する（§1.3 手順 2）。
/// </remarks>
public sealed class ParsingStage
{
    private readonly ChannelReader<RawDatagram> _q1Reader;
    private readonly ChannelWriter<LogRecord> _q2Writer;
    private readonly DiskSpool? _spool;
    private readonly IngestionMetrics _metrics;
    private TimeZoneInfo? _defaultRfc3164TimeZone;
    private readonly ILogger<ParsingStage> _logger;

    /// <param name="q1Reader">受信段からの生データグラム読み取り口。</param>
    /// <param name="q2Writer">永続化段への解析済みレコード書き込み口。</param>
    /// <param name="spool">Q2 満杯・停止時の退避先（縮退運転時は <see langword="null"/>）。</param>
    /// <param name="metrics">計測点。</param>
    /// <param name="defaultRfc3164TimeZone">
    /// RFC 3164 TIMESTAMP の既定タイムゾーン（Issue #134。<see cref="SyslogParser.Parse"/> へ
    /// そのまま渡す）。<see langword="null"/> は UTC（現状互換）。
    /// </param>
    /// <param name="logger">ロガー。</param>
    public ParsingStage(
        ChannelReader<RawDatagram> q1Reader,
        ChannelWriter<LogRecord> q2Writer,
        DiskSpool? spool,
        IngestionMetrics metrics,
        TimeZoneInfo? defaultRfc3164TimeZone = null,
        ILogger<ParsingStage>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(q1Reader);
        ArgumentNullException.ThrowIfNull(q2Writer);
        ArgumentNullException.ThrowIfNull(metrics);

        _q1Reader = q1Reader;
        _q2Writer = q2Writer;
        _spool = spool;
        _metrics = metrics;
        _defaultRfc3164TimeZone = defaultRfc3164TimeZone;
        _logger = logger ?? NullLogger<ParsingStage>.Instance;
    }

    /// <summary>
    /// RFC 3164 TIMESTAMP の既定タイムゾーンを実行中に更新する（設定ライブ再読み込み。
    /// CF-4 層1。Issue #262）。解析は 1 データグラムごとに本フィールドを読んで
    /// <see cref="SyslogParser.Parse"/> へ渡すため、参照の交換だけで次のデータグラムから
    /// 新しい解釈が使われる（解析途中のデータグラムは旧値で一貫して解釈される）。
    /// </summary>
    public void UpdateDefaultRfc3164TimeZone(TimeZoneInfo? timeZone) =>
        Volatile.Write(ref _defaultRfc3164TimeZone, timeZone);

    /// <summary>
    /// Q1 の消費ループ。<paramref name="stoppingToken"/> がキャンセルされたら、以降は
    /// Q1 に残る分を解析したうえで DB を経由せず直接スプールへ退避してから終了する
    /// （architecture.md §1.3 手順 2「メモリ上の未永続化ログを…スプールへ追記して退避する」
    /// ——Q1 上の未解析データも「メモリ上の未永続化ログ」に含まれる）。
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            RawDatagram datagram;
            try
            {
                if (!await _q1Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                if (!_q1Reader.TryRead(out datagram!))
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                // stoppingToken 自体がキャンセルされた——Q1 に残る全件を解析したうえで
                // DB を待たず直接スプールへ退避する（§1.3 手順 2）。
                await DrainRemainderToSpoolAsync().ConfigureAwait(false);
                return;
            }

            var record = SyslogParser.Parse(datagram, _defaultRfc3164TimeZone);

            if (stoppingToken.IsCancellationRequested)
            {
                // 停止要求後は Q2 の状態（相手側も停止処理中）に依らず、直接スプールへ
                // 退避する（§1.3 手順 2）。
                await EvacuateToSpoolAsync(record).ConfigureAwait(false);
                continue;
            }

            if (_q2Writer.TryWrite(record))
            {
                continue;
            }

            // Q2 が満杯——新規投入分をスプールへ退避する（§3.1・§3.2.1）。
            await EvacuateToSpoolAsync(record).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 停止時、Q1 に残っている全データグラムを解析したうえでスプールへ退避する
    /// （architecture.md §1.3 手順 2）。カウンタ計上はレコード単位で完結させる
    /// （<see cref="EvacuateToSpoolAsync"/> 参照）。
    /// </summary>
    private async Task DrainRemainderToSpoolAsync()
    {
        while (_q1Reader.TryRead(out var datagram))
        {
            var record = SyslogParser.Parse(datagram, _defaultRfc3164TimeZone);
            await EvacuateToSpoolAsync(record).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 1 レコードをスプールへ退避する（Q2 溢れ時・停止時の両方から呼ばれる）。
    /// スプールが無い（縮退運転。§1.2）場合、または退避自体が失敗した場合は破棄し
    /// 「永続化失敗」カウンタへ計上する。カウンタ計上をレコード単位で完結させることで、
    /// 退避処理の途中で強制終了しても、それまでに処理済みの分の計上は失われない
    /// （§1.3「退避中に発生した破棄は逐次カウンタへ反映する」）。
    /// </summary>
    private async Task EvacuateToSpoolAsync(LogRecord record)
    {
        if (_spool is null)
        {
            _metrics.RecordPersistenceFailed();
            return;
        }

        var result = await _spool.TryAppendAsync(SpoolRecord.ForLog(record)).ConfigureAwait(false);

        switch (result)
        {
            case SpoolAppendResult.Appended:
                _metrics.RecordSpoolEvacuated();
                break;
            case SpoolAppendResult.QuotaExceeded:
                _metrics.RecordSpoolDiscarded();
                _metrics.RecordPersistenceFailed();
                break;
            case SpoolAppendResult.WriteFailed:
                _logger.LogWarning("スプールへの退避が書き込み失敗のため破棄された。");
                _metrics.RecordSpoolWriteFailed();
                _metrics.RecordPersistenceFailed();
                break;
        }
    }
}
