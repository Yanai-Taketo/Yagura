using System.Buffers;

namespace Yagura.Ingestion.Tcp;

/// <summary>
/// RFC 6587 の 2 方式（octet-counting / non-transparent-framing）を接続単位で判別し、
/// 受信バイト列からメッセージ境界を切り出す状態機械（純粋ロジック。ソケット I/O を持たない）。
/// </summary>
/// <remarks>
/// <para>
/// <b>判別方式</b>（RFC 6587 §3.4.1・§3.4.2）: 接続で最初に受け取ったバイトが数字（'1'〜'9'。
/// §3.4.1 の MSG-LEN は "NONZERO-DIGIT *DIGIT" であり、先頭桁がゼロになることはない）なら
/// octet-counting、それ以外（同 §3.4.2「非透過フレーミングは先頭バイトが '&lt;' (%d60) なら
/// 使われていると仮定してよい」の記述を一般化し、数字以外はすべて non-transparent-framing と
/// みなす）なら non-transparent-framing として、接続が切断されるまで固定する（RFC は接続内で
/// 方式が変わることを想定していない）。
/// </para>
/// <para>
/// <b>Octet-counting</b>（RFC 6587 §3.4.1）: <c>SYSLOG-FRAME = MSG-LEN SP SYSLOG-MSG</c>。
/// MSG-LEN は 10 進数の桁列（先頭は非ゼロ）で、続く 1 個の SP（%d32）の直後から MSG-LEN
/// バイト分がメッセージ本体。本実装は極端に巨大な MSG-LEN（不正・攻撃的な送信元）を
/// <see cref="TcpFrameDecoderOptions.MaxMessageLength"/> で制限する（RFC は上限を規定しないため
/// 実装判断。M4 依頼の「1 接続のメッセージサイズ上限」に対応）。
/// </para>
/// <para>
/// <b>Non-transparent-framing</b>（RFC 6587 §3.4.2）: トレーラーで区切る。RFC は
/// 「最も一般的には ASCII LF」「CR LF の 2 文字トレーラーも観測される」とだけ記す
/// （SHOULD/MUST の規定はない）。本実装は **LF（%d10）を唯一のメッセージ境界**として扱い、
/// メッセージ末尾に単独の CR（%d13）が残っていれば取り除く（CRLF 送信元との相互運用）。
/// メッセージ内部の LF はすべて境界として扱う——RFC 自身が指摘する「メッセージ内 LF は
/// 複数メッセージと誤認識され得る」既知の限界であり、本実装もこれを継承する（判別・分割の
/// 単純さを優先する設計判断）。
/// </para>
/// </remarks>
public sealed class TcpFrameDecoder
{
    private readonly TcpFrameDecoderOptions _options;

    private FramingMode _mode = FramingMode.Undetermined;

    // Non-transparent-framing 用の蓄積バッファ（LF が来るまでの断片を貯める）。
    private readonly ArrayBufferWriter<byte> _lineBuffer = new();

    // Octet-counting 用の状態: 現在 MSG-LEN の桁を読んでいる最中か、既に確定して本体を読んでいるか。
    private readonly List<byte> _lengthDigits = new();
    private int _pendingMessageLength = -1;
    private readonly ArrayBufferWriter<byte> _messageBuffer = new();

    public TcpFrameDecoder(TcpFrameDecoderOptions? options = null)
    {
        _options = options ?? TcpFrameDecoderOptions.Default;
    }

    /// <summary>
    /// 判別済みのフレーミング方式。最初のバイトを処理するまでは <see cref="FramingMode.Undetermined"/>。
    /// </summary>
    public FramingMode Mode => _mode;

    /// <summary>
    /// 接続が切断された時点で読みかけの不完全データが残っているか
    /// （database.md §2.1「不完全は解析失敗に優先」の対象）。
    /// </summary>
    public bool HasPendingIncompleteData =>
        _mode == FramingMode.OctetCounting
            ? _lengthDigits.Count > 0 || _messageBuffer.WrittenCount > 0
            : _lineBuffer.WrittenCount > 0;

    /// <summary>
    /// 受信バイト列を投入し、境界が確定したメッセージをすべて切り出す。
    /// </summary>
    /// <param name="chunk">ソケットから読み取った生バイト列（1 回の Read 分でよい）。</param>
    /// <returns>
    /// このチャンクの処理で境界が確定したメッセージの一覧（0 件のこともある）。
    /// 各要素は解析段へ渡すための独立したバイト配列（呼び出し元がバッファを再利用してよい）。
    /// </returns>
    /// <exception cref="TcpFrameSizeExceededException">
    /// 1 メッセージのサイズが <see cref="TcpFrameDecoderOptions.MaxMessageLength"/> を超えた場合。
    /// 呼び出し元は接続を切断する安全側の扱いを行う（M4 依頼の判断——巨大 octet-count や
    /// LF が来ない stream への防御）。
    /// </exception>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return Array.Empty<byte[]>();
        }

        if (_mode == FramingMode.Undetermined)
        {
            // RFC 6587 §3.4.1/§3.4.2: 接続最初のバイトで方式を判別し、以後固定する。
            _mode = IsAsciiDigit(chunk[0]) ? FramingMode.OctetCounting : FramingMode.NonTransparent;
        }

        return _mode == FramingMode.OctetCounting
            ? PushOctetCounting(chunk)
            : PushNonTransparent(chunk);
    }

    /// <summary>
    /// 接続切断時に呼ぶ。読みかけの不完全データが残っていれば 1 件のメッセージとして返す
    /// （呼び出し元は <see cref="Yagura.Ingestion.Udp.RawDatagram.Incomplete"/> = <c>true</c> として
    /// 扱う）。残っていなければ <c>null</c>。
    /// </summary>
    public byte[]? Flush()
    {
        if (_mode == FramingMode.OctetCounting)
        {
            if (_lengthDigits.Count > 0 && _messageBuffer.WrittenCount == 0 && _pendingMessageLength < 0)
            {
                // MSG-LEN の桁を読んでいる途中（SP にすら到達していない）で切断された。
                var partial = _lengthDigits.ToArray();
                _lengthDigits.Clear();
                return partial;
            }

            if (_messageBuffer.WrittenCount > 0)
            {
                var partial = _messageBuffer.WrittenSpan.ToArray();
                _messageBuffer.Clear();
                return partial;
            }

            return null;
        }

        if (_lineBuffer.WrittenCount > 0)
        {
            var partial = _lineBuffer.WrittenSpan.ToArray();
            _lineBuffer.Clear();
            return partial;
        }

        return null;
    }

    private IReadOnlyList<byte[]> PushNonTransparent(ReadOnlySpan<byte> chunk)
    {
        List<byte[]>? messages = null;

        var remaining = chunk;
        while (true)
        {
            var lfIndex = remaining.IndexOf((byte)'\n');
            if (lfIndex < 0)
            {
                AppendChecked(_lineBuffer, remaining);
                break;
            }

            var linePart = remaining[..lfIndex];
            AppendChecked(_lineBuffer, linePart);

            var line = _lineBuffer.WrittenSpan;
            // CRLF 相互運用: トレーラー直前の単独 CR を取り除く（RFC 6587 §3.4.2 の
            // 「CR LF の 2 文字トレーラーも観測される」に対応）。
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            (messages ??= new List<byte[]>()).Add(line.ToArray());
            _lineBuffer.Clear();

            remaining = remaining[(lfIndex + 1)..];
            if (remaining.IsEmpty)
            {
                break;
            }
        }

        return (IReadOnlyList<byte[]>?)messages ?? Array.Empty<byte[]>();
    }

    private IReadOnlyList<byte[]> PushOctetCounting(ReadOnlySpan<byte> chunk)
    {
        List<byte[]>? messages = null;

        var remaining = chunk;
        while (!remaining.IsEmpty)
        {
            if (_pendingMessageLength < 0)
            {
                // MSG-LEN の桁を集めている段階。SP (%d32) が来たら桁確定。
                var spIndex = remaining.IndexOf((byte)' ');
                if (spIndex < 0)
                {
                    AppendLengthDigits(remaining);
                    break;
                }

                AppendLengthDigits(remaining[..spIndex]);
                remaining = remaining[(spIndex + 1)..];

                _pendingMessageLength = ParseMessageLength(_lengthDigits);
                _lengthDigits.Clear();

                if (_pendingMessageLength > _options.MaxMessageLength)
                {
                    throw new TcpFrameSizeExceededException(
                        $"octet-counting の MSG-LEN {_pendingMessageLength} が上限 {_options.MaxMessageLength} を超えている。");
                }

                continue;
            }

            var needed = _pendingMessageLength - _messageBuffer.WrittenCount;
            var take = Math.Min(needed, remaining.Length);
            _messageBuffer.Write(remaining[..take]);
            remaining = remaining[take..];

            if (_messageBuffer.WrittenCount == _pendingMessageLength)
            {
                (messages ??= new List<byte[]>()).Add(_messageBuffer.WrittenSpan.ToArray());
                _messageBuffer.Clear();
                _pendingMessageLength = -1;
            }
        }

        return (IReadOnlyList<byte[]>?)messages ?? Array.Empty<byte[]>();
    }

    private void AppendLengthDigits(ReadOnlySpan<byte> digits)
    {
        foreach (var b in digits)
        {
            // MSG-LEN の 2 桁目以降は '0'〜'9' すべてを許す（RFC 6587 §3.4.1 の ABNF
            // "MSG-LEN = NONZERO-DIGIT *DIGIT"——非ゼロなのは先頭桁のみ）。
            // 判別に使う IsAsciiDigit（先頭バイト判定用、'1'〜'9' のみ）とは別の判定にする。
            if (b is < (byte)'0' or > (byte)'9')
            {
                throw new TcpFrameSizeExceededException(
                    "octet-counting の MSG-LEN に数字以外のバイトが含まれている（不正なフレーム）。");
            }

            _lengthDigits.Add(b);

            // 桁数自体が異常に多い場合も早期に打ち切る（数値オーバーフロー・DoS 対策）。
            if (_lengthDigits.Count > MaxLengthDigits)
            {
                throw new TcpFrameSizeExceededException(
                    "octet-counting の MSG-LEN の桁数が上限を超えている（不正なフレーム）。");
            }
        }
    }

    private static int ParseMessageLength(List<byte> digits)
    {
        var length = 0;
        foreach (var b in digits)
        {
            length = checked(length * 10 + (b - (byte)'0'));
        }

        return length;
    }

    private void AppendChecked(ArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty)
        {
            buffer.Write(data);
        }

        if (buffer.WrittenCount > _options.MaxMessageLength)
        {
            throw new TcpFrameSizeExceededException(
                $"non-transparent-framing のメッセージが上限 {_options.MaxMessageLength} バイトを超えている" +
                "（LF が来ないストリームへの防御）。");
        }
    }

    private static bool IsAsciiDigit(byte b) => b is >= (byte)'1' and <= (byte)'9';

    // 桁数上限: int.MaxValue は 10 桁なので、11 桁あれば確実に妥当な MSG-LEN の範囲を超える。
    private const int MaxLengthDigits = 10;
}

/// <summary>
/// <see cref="TcpFrameDecoder"/> の判別結果。
/// </summary>
public enum FramingMode
{
    /// <summary>まだ最初のバイトを受け取っていない。</summary>
    Undetermined,

    /// <summary>RFC 6587 §3.4.1。</summary>
    OctetCounting,

    /// <summary>RFC 6587 §3.4.2。</summary>
    NonTransparent,
}
