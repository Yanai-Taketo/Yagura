using System.Threading.Channels;
using Yagura.Ingestion.Udp;
using Yagura.Storage;

namespace Yagura.Ingestion.Parsing;

/// <summary>
/// 解析段の消費ループ（architecture.md §2.1）。Q1 から <see cref="RawDatagram"/> を取り出し、
/// <see cref="MinimalSyslogParser"/> で解析して Q2 へ投入する。
/// </summary>
/// <remarks>
/// Q2 は有限キューだが、解析段 → 永続化段の境界の溢れ時挙動は「新規投入分をスプールへ退避」
/// である（architecture.md §3.1）。スプールは M4 で実装するため、M2 時点は Q2 の
/// <see cref="ChannelWriter{T}.WriteAsync"/> を await してバックプレッシャをかける
/// （投入をブロックする——スプールが無い間の暫定挙動。データを破棄しない）。
/// </remarks>
public sealed class ParsingStage
{
    private readonly ChannelReader<RawDatagram> _q1Reader;
    private readonly ChannelWriter<LogRecord> _q2Writer;

    public ParsingStage(ChannelReader<RawDatagram> q1Reader, ChannelWriter<LogRecord> q2Writer)
    {
        ArgumentNullException.ThrowIfNull(q1Reader);
        ArgumentNullException.ThrowIfNull(q2Writer);

        _q1Reader = q1Reader;
        _q2Writer = q2Writer;
    }

    /// <summary>
    /// Q1 の消費ループ。<paramref name="stoppingToken"/> がキャンセルされ、かつ Q1 が
    /// 空になるまで実行する（停止時のベストエフォート drain。architecture.md §1.3 手順 2）。
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
                return;
            }

            var record = MinimalSyslogParser.Parse(datagram);

            // stoppingToken は渡さない: 停止要求後も、Q1 から取り出し済みのレコードは
            // Q2 へ渡し切る（drain のベストエフォート。§1.3）。
            await _q2Writer.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
