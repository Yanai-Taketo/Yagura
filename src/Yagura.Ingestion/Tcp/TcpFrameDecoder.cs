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
/// 実装判断。M4 依頼の「1 接続のメッセージサイズ上限」に対応）。**上限超過時は接続を切断せず、
/// 当該メッセージの本体（宣言済みの MSG-LEN 分）だけを読み飛ばして破棄し、次のフレームから
/// 受信を継続する**（Issue #143。破棄は <see cref="OversizedMessagesDiscardedCount"/> で計上する
/// ——計測 API を持たない本クラスに代わり、呼び出し元がこの値の差分をカウンタへ計上する）。
/// また、フレーム間（本体を読み切った直後、次の MSG-LEN 桁を読み始める前）に紛れ込んだ
/// LF（%d10）・CR（%d13）は寛容にスキップして再同期する（Issue #143。相互運用で余分な改行を
/// 挟む送信元が現実に存在するため）。MSG-LEN の桁の途中に数字以外のバイトが現れる、桁数が
/// 異常、または int の範囲を超える等、再同期できない深刻な破損のみ
/// <see cref="TcpFrameSizeExceededException"/> を送出し、呼び出し元が接続を切断する。
/// </para>
/// <para>
/// <b>Non-transparent-framing</b>（RFC 6587 §3.4.2）: トレーラーで区切る。RFC は
/// 「最も一般的には ASCII LF」「CR LF の 2 文字トレーラーも観測される」とだけ記す
/// （SHOULD/MUST の規定はない）。本実装は **LF（%d10）を唯一のメッセージ境界**として扱い、
/// メッセージ末尾に単独の CR（%d13）が残っていれば取り除く（CRLF 送信元との相互運用）。
/// メッセージ内部の LF はすべて境界として扱う——RFC 自身が指摘する「メッセージ内 LF は
/// 複数メッセージと誤認識され得る」既知の限界であり、本実装もこれを継承する（判別・分割の
/// 単純さを優先する設計判断）。**1 行が <see cref="TcpFrameDecoderOptions.MaxMessageLength"/>
/// を超えた場合も接続は切断しない**——それまでに蓄積した断片を破棄し、直後の LF まで読み飛ばして
/// 1 行分の破棄として扱い、次の行から通常運転に戻る（Issue #143。破棄は
/// <see cref="OversizedMessagesDiscardedCount"/> で計上する）。
/// </para>
/// <para>
/// <b>再同期バイト数上限</b>（PR #169 レビュー指摘 3 へのオーナー決定 2026-07-09）: 上記の
/// 寛容な読み飛ばし（フレーム間 LF/CR のスキップ・上限超過メッセージ本体の読み飛ばし・
/// non-transparent-framing の破棄行）には、**有効なメッセージが 1 件確定するたびにリセット
/// される累計バイト数の天井**（<see cref="TcpFrameDecoderOptions.MaxResyncBytes"/>。既定
/// 128 KiB）を設ける。他社実装（rsyslog・syslog-ng・Fluent Bit 等）はフレーミングエラー
/// 即切断が主流であり、Issue #143 の寛容化はこの一次防御を外した状態になるため、天井との組で
/// 同等の防御水準を保つ。超過時は <see cref="TcpFrameViolationKind.ResyncByteLimitExceeded"/>
/// の <see cref="TcpFrameSizeExceededException"/> を送出し、呼び出し元が接続を切断する。
/// </para>
/// </remarks>
public sealed class TcpFrameDecoder
{
    private readonly TcpFrameDecoderOptions _options;

    private FramingMode _mode = FramingMode.Undetermined;

    // Non-transparent-framing 用の蓄積バッファ（LF が来るまでの断片を貯める）。
    private readonly ArrayBufferWriter<byte> _lineBuffer = new();

    // Issue #143: 現在の行が上限超過で破棄対象になっており、次の LF まで読み飛ばし中か。
    private bool _discardingOversizedLine;

    // Octet-counting 用の状態: 現在 MSG-LEN の桁を読んでいる最中か、既に確定して本体を読んでいるか。
    private readonly List<byte> _lengthDigits = new();
    private int _pendingMessageLength = -1;
    private readonly ArrayBufferWriter<byte> _messageBuffer = new();

    // Issue #143: 上限超過と判明したメッセージの残り読み飛ばしバイト数（-1 = 読み飛ばし中でない）。
    // 宣言された MSG-LEN 分をバッファへ蓄積せずに読み捨てることで、無制限のメモリ確保を避ける。
    private int _oversizedSkipRemaining = -1;

    // Issue #143: これまでにサイズ上限超過で破棄したメッセージの累積数（OctetCounting・
    // NonTransparent の両方式で共有する。OversizedMessagesDiscardedCount 参照）。
    private int _oversizedMessagesDiscarded;

    // PR #169 レビュー指摘 3 へのオーナー決定（2026-07-09）対応: 有効なメッセージが 1 件も
    // 確定しないまま読み捨てたバイト数の累積（フレーム間 LF/CR のスキップ・上限超過メッセージ
    // 本体の読み飛ばし・non-transparent-framing の破棄行）。メッセージが 1 件確定するたびに
    // 0 へリセットする。TcpFrameDecoderOptions.MaxResyncBytes を超えたら「再同期の見込みが
    // ないゴミデータ」として ResyncByteLimitExceeded の例外を送出する（呼び出し元が切断する）。
    private long _resyncDiscardedBytes;

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
    /// これまでにサイズ上限超過で破棄したメッセージの累積数（Issue #143）。呼び出し元
    /// （<see cref="Yagura.Ingestion.Tcp.TcpSyslogListener"/>）は <see cref="Push"/> 呼び出しの
    /// 前後でこの値の差分を読み取り、カウンタ・ログへ計上する——本クラスはソケット I/O・計測 API
    /// を持たない純粋ロジックであるため、計上そのものは呼び出し元の責務とする。
    /// </summary>
    public int OversizedMessagesDiscardedCount => _oversizedMessagesDiscarded;

    /// <summary>
    /// 受信バイト列を投入し、境界が確定したメッセージをすべて切り出す。
    /// </summary>
    /// <param name="chunk">ソケットから読み取った生バイト列（1 回の Read 分でよい）。</param>
    /// <returns>
    /// このチャンクの処理で境界が確定したメッセージの一覧（0 件のこともある）。
    /// 各要素は解析段へ渡すための独立したバイト配列（呼び出し元がバッファを再利用してよい）。
    /// </returns>
    /// <exception cref="TcpFrameSizeExceededException">
    /// 次のいずれかの回復不能なフレーミング違反の場合（種別は
    /// <see cref="TcpFrameSizeExceededException.Kind"/>）:
    /// ① octet-counting の MSG-LEN が再同期不能な形式で壊れている（数字以外のバイトの混入・
    /// 桁数異常・int 範囲超過。<see cref="TcpFrameViolationKind.UnrecoverableCorruption"/>）、
    /// ② 有効なメッセージが 1 件も確定しないまま読み捨てたバイト数が
    /// <see cref="TcpFrameDecoderOptions.MaxResyncBytes"/> を超えた
    /// （<see cref="TcpFrameViolationKind.ResyncByteLimitExceeded"/>。オーナー決定 2026-07-09）。
    /// 1 メッセージのサイズ上限超過（MSG-LEN 自体は正しく読み取れる、または
    /// non-transparent-framing の 1 行が上限超過の場合）はこの例外を送出せず、当該
    /// メッセージだけを破棄して接続を維持する（Issue #143。<see cref="OversizedMessagesDiscardedCount"/>
    /// で計上する）。呼び出し元は本例外で接続を切断する。例外送出までに同一チャンク内で境界が
    /// 確定していた正常メッセージは <see cref="TcpFrameSizeExceededException.CompletedMessages"/>
    /// に載せて引き渡す——呼び出し元は切断前にこれらを Q1 へ流すこと（確定済みメッセージの
    /// 無計上な喪失を防ぐ）。
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

            // Issue #143: 上限超過メッセージの読み飛ばし中に切断された場合は Incomplete として
            // 復元しない——当該メッセージは既に破棄が確定しており、救うべきデータではない。
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

        try
        {
            var remaining = chunk;
            while (!remaining.IsEmpty)
            {
                var lfIndex = remaining.IndexOf((byte)'\n');

                if (_discardingOversizedLine)
                {
                    if (lfIndex < 0)
                    {
                        // 破棄対象の行がこのチャンクでも終端しない。バッファには積まず読み捨てる
                        // （読み捨ては再同期バイト数上限の対象——オーナー決定 2026-07-09）。
                        CountResyncDiscardedBytes(remaining.Length);
                        break;
                    }

                    // 破棄対象の行がここで終端した。次のバイトから通常運転に戻る。
                    CountResyncDiscardedBytes(lfIndex + 1);
                    _discardingOversizedLine = false;
                    remaining = remaining[(lfIndex + 1)..];
                    continue;
                }

                if (lfIndex < 0)
                {
                    if (!TryAppendLine(remaining))
                    {
                        var discardedBytes = _lineBuffer.WrittenCount + remaining.Length;
                        StartDiscardingOversizedLine();
                        CountResyncDiscardedBytes(discardedBytes);
                    }

                    break;
                }

                var linePart = remaining[..lfIndex];
                if (!TryAppendLine(linePart))
                {
                    // このチャンク内に LF があるため、破棄対象の行はここで完結する
                    // （Issue #143: 上限超過も接続は切断せず、次の行から通常運転に戻る）。
                    var discardedBytes = _lineBuffer.WrittenCount + lfIndex + 1;
                    _lineBuffer.Clear();
                    _oversizedMessagesDiscarded++;
                    CountResyncDiscardedBytes(discardedBytes);
                    remaining = remaining[(lfIndex + 1)..];
                    continue;
                }

                var line = _lineBuffer.WrittenSpan;
                // CRLF 相互運用: トレーラー直前の単独 CR を取り除く（RFC 6587 §3.4.2 の
                // 「CR LF の 2 文字トレーラーも観測される」に対応）。
                if (line.Length > 0 && line[^1] == (byte)'\r')
                {
                    line = line[..^1];
                }

                (messages ??= new List<byte[]>()).Add(line.ToArray());
                _lineBuffer.Clear();

                // 有効なメッセージが 1 件確定した——再同期バイト数のカウントをリセットする
                //（正常な送信元が散発的な破棄で切断へ追い込まれないための天井のリセット規則。
                // オーナー決定 2026-07-09）。
                _resyncDiscardedBytes = 0;

                remaining = remaining[(lfIndex + 1)..];
            }
        }
        catch (TcpFrameSizeExceededException ex) when (messages is not null)
        {
            // PushOctetCounting と同じ理由: 例外送出までに確定していた正常メッセージを
            // 例外に載せて呼び出し元へ引き渡す（無計上の喪失を防ぐ）。
            ex.CompletedMessages = messages;
            throw;
        }

        return (IReadOnlyList<byte[]>?)messages ?? Array.Empty<byte[]>();
    }

    private IReadOnlyList<byte[]> PushOctetCounting(ReadOnlySpan<byte> chunk)
    {
        List<byte[]>? messages = null;

        try
        {
            var remaining = chunk;
            while (!remaining.IsEmpty)
            {
                if (_oversizedSkipRemaining >= 0)
                {
                    // Issue #143: 上限超過と判明済みのメッセージ本体を読み捨てている最中
                    // （読み捨ては再同期バイト数上限の対象——オーナー決定 2026-07-09。
                    // 上限超過メッセージだけを送り続けて接続を占有し続ける経路の天井）。
                    var skip = Math.Min(_oversizedSkipRemaining, remaining.Length);
                    remaining = remaining[skip..];
                    _oversizedSkipRemaining -= skip;
                    if (_oversizedSkipRemaining == 0)
                    {
                        _oversizedSkipRemaining = -1;
                    }

                    CountResyncDiscardedBytes(skip);
                    continue;
                }

                if (_pendingMessageLength < 0)
                {
                    if (_lengthDigits.Count == 0)
                    {
                        // Issue #143: 新しい MSG-LEN の先頭に紛れ込んだ LF/CR（フレーム間の余分
                        // バイト・空行）を寛容にスキップして再同期する。数字が現れるまで読み飛ばす。
                        var skipped = 0;
                        while (skipped < remaining.Length && IsInterFrameSeparator(remaining[skipped]))
                        {
                            skipped++;
                        }

                        if (skipped > 0)
                        {
                            // スキップも読み捨てであり、再同期バイト数上限の対象
                            //（LF/CR だけを延々と送り続ける接続への天井——オーナー決定 2026-07-09）。
                            CountResyncDiscardedBytes(skipped);
                            remaining = remaining[skipped..];
                            continue;
                        }
                    }

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
                        // Issue #143: 接続は切断せず、宣言済みの本体だけを読み飛ばして破棄する。
                        _oversizedSkipRemaining = _pendingMessageLength;
                        _pendingMessageLength = -1;
                        _oversizedMessagesDiscarded++;
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

                    // 有効なメッセージが 1 件確定した——再同期バイト数のカウントをリセットする
                    //（オーナー決定 2026-07-09 のリセット規則。PushNonTransparent 側と同じ）。
                    _resyncDiscardedBytes = 0;
                }
            }
        }
        catch (TcpFrameSizeExceededException ex) when (messages is not null)
        {
            // PR #169 レビュー指摘 2 への対応: 例外送出までに同一チャンク内で境界が確定していた
            // 正常メッセージを例外に載せて呼び出し元へ引き渡す。ここで載せずに例外だけを伝播
            // させると、確定済みメッセージが Q1 未到達・Incomplete 復元なし・カウンタ計上なしの
            // まま黙って消える（「損失は必ずどれかのカウンタに計上される」§3.1 の原則違反）。
            // Issue #143 でサイズ上限超過が例外を投げなくなったことにより、1 チャンク内に
            // 「複数の正常メッセージ + 末尾の再同期不能な破損」が同居する状況が従来より
            // 起きやすくなったため、この経路の手当てが必要になった。
            ex.CompletedMessages = messages;
            throw;
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
                // Issue #143: MSG-LEN の桁の途中（既に 1 桁以上を読んでいる状態）で数字以外が
                // 現れるのは、フレーム間の空行スキップでは救えない再同期不能な破損である。
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
        try
        {
            var length = 0;
            foreach (var b in digits)
            {
                length = checked(length * 10 + (b - (byte)'0'));
            }

            return length;
        }
        catch (OverflowException)
        {
            // MaxLengthDigits(10 桁)以内でも int.MaxValue を超える値はあり得る
            //（例: "9999999999"）。OverflowException のまま伝播させると、呼び出し側の
            // 安全側経路（TcpSyslogListener の catch）を通らず、接続タスクの fault として
            // StopAsync の Task.WhenAll まで届いて停止処理を壊すため、ここで変換する。
            throw new TcpFrameSizeExceededException(
                "octet-counting の MSG-LEN が int の範囲を超えている（不正なフレーム）。");
        }
    }

    /// <summary>
    /// <paramref name="data"/> を <see cref="_lineBuffer"/> へ追記する。追記後に
    /// <see cref="TcpFrameDecoderOptions.MaxMessageLength"/> を超える場合は追記せず <c>false</c>
    /// を返す（Issue #143: 呼び出し元が破棄状態へ遷移させる。例外は投げない）。
    /// </summary>
    private bool TryAppendLine(ReadOnlySpan<byte> data)
    {
        if (_lineBuffer.WrittenCount + data.Length > _options.MaxMessageLength)
        {
            return false;
        }

        if (!data.IsEmpty)
        {
            _lineBuffer.Write(data);
        }

        return true;
    }

    /// <summary>
    /// non-transparent-framing の現在行が上限超過と判明した際に呼ぶ。蓄積済みの断片を破棄し、
    /// 破棄カウンタを計上したうえで、次の LF まで読み飛ばす状態へ遷移する（Issue #143）。
    /// </summary>
    private void StartDiscardingOversizedLine()
    {
        _lineBuffer.Clear();
        _discardingOversizedLine = true;
        _oversizedMessagesDiscarded++;
    }

    /// <summary>
    /// 読み捨てバイト数を加算し、再同期バイト数上限（<see cref="TcpFrameDecoderOptions.MaxResyncBytes"/>）
    /// を超えたら <see cref="TcpFrameViolationKind.ResyncByteLimitExceeded"/> の例外を送出する
    /// （PR #169 レビュー指摘 3 へのオーナー決定 2026-07-09。有効なメッセージが確定するたびに
    /// カウントは 0 へ戻る——リセットは各 Push 内のメッセージ確定箇所で行う）。
    /// </summary>
    private void CountResyncDiscardedBytes(int count)
    {
        if (_options.MaxResyncBytes <= 0)
        {
            // 無効化（主にテスト用途）。
            return;
        }

        _resyncDiscardedBytes += count;
        if (_resyncDiscardedBytes > _options.MaxResyncBytes)
        {
            throw new TcpFrameSizeExceededException(
                $"有効なメッセージが確定しないまま読み捨てたバイト数が上限 {_options.MaxResyncBytes} を超えた" +
                "（再同期の見込みがないストリームへの防御）。",
                TcpFrameViolationKind.ResyncByteLimitExceeded);
        }
    }

    private static bool IsAsciiDigit(byte b) => b is >= (byte)'1' and <= (byte)'9';

    // Issue #143: octet-counting のフレーム間（本体を読み切った直後、次の MSG-LEN 桁を読み始める
    // 前）に紛れ込んだ LF/CR を再同期のために寛容にスキップする対象。空白（SP）は MSG-LEN と
    // 本体の区切りそのものとして意味を持つため対象に含めない。
    private static bool IsInterFrameSeparator(byte b) => b is (byte)'\n' or (byte)'\r';

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
