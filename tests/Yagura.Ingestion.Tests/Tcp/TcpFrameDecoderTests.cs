using System.Text;
using Yagura.Ingestion.Tcp;

namespace Yagura.Ingestion.Tests.Tcp;

/// <summary>
/// <see cref="TcpFrameDecoder"/> の単体テスト（RFC 6587 §3.4.1・§3.4.2。M4-1）。
/// </summary>
public sealed class TcpFrameDecoderTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    // ------------------------------------------------------------------
    // 判別（RFC 6587 §3.4.1/§3.4.2: 先頭バイトが数字なら octet-counting、それ以外は non-transparent）
    // ------------------------------------------------------------------

    [Fact]
    public void Push_FirstByteIsDigit_DeterminesOctetCounting()
    {
        var decoder = new TcpFrameDecoder();

        decoder.Push(Ascii("5 "));

        Assert.Equal(FramingMode.OctetCounting, decoder.Mode);
    }

    [Fact]
    public void Push_FirstByteIsNotDigit_DeterminesNonTransparent()
    {
        var decoder = new TcpFrameDecoder();

        decoder.Push(Ascii("<34>hello"));

        Assert.Equal(FramingMode.NonTransparent, decoder.Mode);
    }

    [Fact]
    public void Mode_BeforeAnyData_IsUndetermined()
    {
        var decoder = new TcpFrameDecoder();

        Assert.Equal(FramingMode.Undetermined, decoder.Mode);
    }

    // ------------------------------------------------------------------
    // Octet-counting（RFC 6587 §3.4.1: SYSLOG-FRAME = MSG-LEN SP SYSLOG-MSG）
    // ------------------------------------------------------------------

    [Fact]
    public void Push_OctetCounting_SingleFrameInOneChunk_ExtractsMessage()
    {
        var decoder = new TcpFrameDecoder();
        // "<34>hello" は 9 オクテット。
        var frame = Ascii("9 <34>hello");

        var messages = decoder.Push(frame);

        Assert.Single(messages);
        Assert.Equal("<34>hello", Encoding.ASCII.GetString(messages[0]));
    }

    [Fact]
    public void Push_OctetCounting_MultipleMessagesConcatenated_ExtractsBoth()
    {
        var decoder = new TcpFrameDecoder();
        // "5 abcde" + "3 xyz" が 1 回の Read で連結して届く典型パターン。
        var frame = Ascii("5 abcde3 xyz");

        var messages = decoder.Push(frame);

        Assert.Equal(2, messages.Count);
        Assert.Equal("abcde", Encoding.ASCII.GetString(messages[0]));
        Assert.Equal("xyz", Encoding.ASCII.GetString(messages[1]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Push_OctetCounting_SplitAcrossChunksAtEveryBoundary_ExtractsMessage(int splitAt)
    {
        // "5 abcde" を 1 バイトずつ様々な位置で分割着信させても、最終的に 1 件抽出できること
        // （MSG-LEN の桁の途中・SP の直後・本体の途中、いずれの分割点でも壊れないこと）。
        var decoder = new TcpFrameDecoder();
        var frame = Ascii("5 abcde");

        var collected = new List<byte[]>();
        var first = frame[..splitAt];
        var second = frame[splitAt..];

        collected.AddRange(decoder.Push(first));
        collected.AddRange(decoder.Push(second));

        Assert.Single(collected);
        Assert.Equal("abcde", Encoding.ASCII.GetString(collected[0]));
    }

    [Fact]
    public void Push_OctetCounting_MultiDigitLength_ExtractsMessage()
    {
        var decoder = new TcpFrameDecoder();
        var body = new string('a', 123);
        var frame = Ascii($"123 {body}");

        var messages = decoder.Push(frame);

        Assert.Single(messages);
        Assert.Equal(body, Encoding.ASCII.GetString(messages[0]));
    }

    [Fact]
    public void Push_OctetCounting_MessageLengthOverflowsInt_ThrowsFrameSizeExceededNotOverflow()
    {
        var decoder = new TcpFrameDecoder();

        // 10 桁以内でも int.MaxValue を超える MSG-LEN（例: 9999999999)がある。
        // OverflowException のまま漏れると TcpSyslogListener の安全側経路（切断 +
        // Incomplete 退避）を通らず接続タスクが fault し、StopAsync の Task.WhenAll で
        // 停止処理が壊れるため、TcpFrameSizeExceededException へ変換されることを固定化する。
        Assert.Throws<TcpFrameSizeExceededException>(() => decoder.Push(Ascii("9999999999 ")));
    }

    [Fact]
    public void AppendLengthDigits_NonDigitMidNumber_ThrowsFrameSizeExceeded()
    {
        // Issue #143: フレーム間の LF/CR は再同期の対象だが、MSG-LEN の桁の途中
        // （既に 1 桁以上を読んでいる状態）に現れる数字以外のバイトは、再同期できない
        // 深刻な破損として例外を維持する。
        var decoder = new TcpFrameDecoder();

        Assert.Throws<TcpFrameSizeExceededException>(() => decoder.Push(Ascii("1x2 body")));
    }

    [Fact]
    public void Push_OctetCounting_CorruptionAfterCompletedMessages_ExceptionCarriesCompletedMessages()
    {
        // PR #169 レビュー指摘 2: 1 チャンク内に「複数の正常フレーム + 末尾に再同期不能な破損」が
        // 同居した場合、例外送出までに境界が確定していた正常メッセージが例外に載って引き渡される
        // こと（例外とともに黙って消えると、Q1 未到達・カウンタ計上なしの無計上な喪失になる——
        // architecture.md §3.1「損失は必ずどれかのカウンタに計上される」の原則違反）。
        var decoder = new TcpFrameDecoder();

        var ex = Assert.Throws<TcpFrameSizeExceededException>(
            () => decoder.Push(Ascii("5 abcde3 xyz1x2 broken")));

        Assert.Equal(2, ex.CompletedMessages.Count);
        Assert.Equal("abcde", Encoding.ASCII.GetString(ex.CompletedMessages[0]));
        Assert.Equal("xyz", Encoding.ASCII.GetString(ex.CompletedMessages[1]));
    }

    [Fact]
    public void Push_OctetCounting_CorruptionWithoutCompletedMessages_ExceptionCarriesEmptyList()
    {
        // 確定済みメッセージが 1 件もない場合は空リストのまま（null にならないことの固定化）。
        var decoder = new TcpFrameDecoder();

        var ex = Assert.Throws<TcpFrameSizeExceededException>(() => decoder.Push(Ascii("1x2 body")));

        Assert.Empty(ex.CompletedMessages);
    }

    // ------------------------------------------------------------------
    // Issue #143: 1 メッセージのサイズ上限超過 — 接続を切断せず当該メッセージのみ破棄する
    // ------------------------------------------------------------------

    [Fact]
    public void Push_OctetCounting_MessageLengthExceedsMax_DoesNotThrow()
    {
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 10 });

        var messages = decoder.Push(Ascii("100 "));

        Assert.Empty(messages);
    }

    [Fact]
    public void Push_OctetCounting_MessageLengthExceedsMax_IncrementsOversizedDiscardedCount()
    {
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 10 });

        decoder.Push(Ascii("100 "));

        Assert.Equal(1, decoder.OversizedMessagesDiscardedCount);
    }

    [Fact]
    public void Push_OctetCounting_OversizedMessage_SkipsBodyAndResumesWithNextFrame()
    {
        // 上限 5 バイトに対し、MSG-LEN 20 の本体をまるごと読み飛ばした後、続く正常なフレーム
        // （"5 abcde"）が同一チャンク内でも正しく抽出できることを確認する（接続は維持される）。
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 5 });
        var oversizedBody = new string('x', 20);
        var frame = Ascii($"20 {oversizedBody}5 abcde");

        var messages = decoder.Push(frame);

        Assert.Single(messages);
        Assert.Equal("abcde", Encoding.ASCII.GetString(messages[0]));
        Assert.Equal(1, decoder.OversizedMessagesDiscardedCount);
    }

    [Fact]
    public void Push_OctetCounting_OversizedMessageBodySplitAcrossChunks_SkipsEntirely()
    {
        // 読み飛ばし対象の本体が複数チャンクに分割されて届いても、正しく読み飛ばしきれること。
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 5 });

        var firstChunkMessages = decoder.Push(Ascii("20 aaaaaaaaaa")); // MSG-LEN 宣言 + 本体 10 バイト
        var secondChunkMessages = decoder.Push(Ascii("aaaaaaaaaa5 abcde")); // 残り 10 バイト + 次フレーム

        Assert.Empty(firstChunkMessages);
        Assert.Single(secondChunkMessages);
        Assert.Equal("abcde", Encoding.ASCII.GetString(secondChunkMessages[0]));
        Assert.Equal(1, decoder.OversizedMessagesDiscardedCount);
    }

    [Fact]
    public void Push_OctetCounting_MultipleOversizedMessages_DiscardsEachIndependently()
    {
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 5 });
        var frame = Ascii($"10 {new string('x', 10)}10 {new string('y', 10)}5 abcde");

        var messages = decoder.Push(frame);

        Assert.Single(messages);
        Assert.Equal("abcde", Encoding.ASCII.GetString(messages[0]));
        Assert.Equal(2, decoder.OversizedMessagesDiscardedCount);
    }

    [Fact]
    public void Push_OctetCounting_InterFrameLfCr_ResyncsAndExtractsNextMessage()
    {
        // Issue #143: 本体を読み切った直後に紛れ込んだ LF/CR（フレーム間の余分バイト）を
        // 寛容にスキップして次のフレームへ再同期できること。
        var decoder = new TcpFrameDecoder();
        var frame = Ascii("5 abcde\r\n5 fghij");

        var messages = decoder.Push(frame);

        Assert.Equal(2, messages.Count);
        Assert.Equal("abcde", Encoding.ASCII.GetString(messages[0]));
        Assert.Equal("fghij", Encoding.ASCII.GetString(messages[1]));
    }

    [Fact]
    public void Push_OctetCounting_InterFrameSeparatorsSplitAcrossChunks_Resyncs()
    {
        var decoder = new TcpFrameDecoder();

        var firstChunkMessages = decoder.Push(Ascii("5 abcde\r"));
        var secondChunkMessages = decoder.Push(Ascii("\n5 fghij"));

        Assert.Single(firstChunkMessages);
        Assert.Single(secondChunkMessages);
        Assert.Equal("abcde", Encoding.ASCII.GetString(firstChunkMessages[0]));
        Assert.Equal("fghij", Encoding.ASCII.GetString(secondChunkMessages[0]));
    }

    [Fact]
    public void Flush_OctetCounting_DisconnectedWhileSkippingOversizedBody_ReturnsNull()
    {
        // 上限超過メッセージの読み飛ばし中に切断されても、既に破棄が確定しているデータなので
        // Incomplete としては復元しない。
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 5 });
        decoder.Push(Ascii("20 only-part"));

        var flushed = decoder.Flush();

        Assert.Null(flushed);
    }

    // ------------------------------------------------------------------
    // Non-transparent-framing（RFC 6587 §3.4.2: LF 区切り。CRLF・LF 混在）
    // ------------------------------------------------------------------

    [Fact]
    public void Push_NonTransparent_LfDelimited_ExtractsMessage()
    {
        var decoder = new TcpFrameDecoder();
        var frame = Ascii("<34>hello\n");

        var messages = decoder.Push(frame);

        Assert.Single(messages);
        Assert.Equal("<34>hello", Encoding.ASCII.GetString(messages[0]));
    }

    [Fact]
    public void Push_NonTransparent_CrLfDelimited_StripsTrailingCr()
    {
        var decoder = new TcpFrameDecoder();
        var frame = Ascii("<34>hello\r\n");

        var messages = decoder.Push(frame);

        Assert.Single(messages);
        Assert.Equal("<34>hello", Encoding.ASCII.GetString(messages[0]));
    }

    [Fact]
    public void Push_NonTransparent_MixedLfAndCrLf_ExtractsBothMessagesCorrectly()
    {
        var decoder = new TcpFrameDecoder();
        var frame = Ascii("<34>first\n<34>second\r\n");

        var messages = decoder.Push(frame);

        Assert.Equal(2, messages.Count);
        Assert.Equal("<34>first", Encoding.ASCII.GetString(messages[0]));
        Assert.Equal("<34>second", Encoding.ASCII.GetString(messages[1]));
    }

    [Fact]
    public void Push_NonTransparent_SplitAcrossChunksAtLf_ExtractsMessage()
    {
        var decoder = new TcpFrameDecoder();

        var firstChunkMessages = decoder.Push(Ascii("<34>hel"));
        var secondChunkMessages = decoder.Push(Ascii("lo\n"));

        Assert.Empty(firstChunkMessages);
        Assert.Single(secondChunkMessages);
        Assert.Equal("<34>hello", Encoding.ASCII.GetString(secondChunkMessages[0]));
    }

    [Fact]
    public void Push_NonTransparent_NoTrailerYet_ReturnsNoMessages()
    {
        var decoder = new TcpFrameDecoder();

        var messages = decoder.Push(Ascii("<34>still-buffering"));

        Assert.Empty(messages);
    }

    // ------------------------------------------------------------------
    // Issue #143: non-transparent-framing の 1 行が上限超過 — 接続を切断せず当該行のみ破棄する
    // ------------------------------------------------------------------

    [Fact]
    public void Push_NonTransparent_MessageExceedsMaxWithoutLf_DoesNotThrow()
    {
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 8 });

        // 先頭が数字以外（non-transparent-framing 判別）で、LF が来ないまま上限を超えても
        // 接続は切断しない（Issue #143）。
        var messages = decoder.Push(Ascii("<34>123456789"));

        Assert.Empty(messages);
    }

    [Fact]
    public void Push_NonTransparent_OversizedLine_LfInSameChunk_DiscardsAndResumesWithNextLine()
    {
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 8 });
        var frame = Ascii("<34>0123456789\n<34>ok\n");

        var messages = decoder.Push(frame);

        Assert.Single(messages);
        Assert.Equal("<34>ok", Encoding.ASCII.GetString(messages[0]));
        Assert.Equal(1, decoder.OversizedMessagesDiscardedCount);
    }

    [Fact]
    public void Push_NonTransparent_OversizedLine_LfArrivesInLaterChunk_DiscardsAndResumes()
    {
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 8 });

        // 上限超過を検出した時点ではまだ LF が来ていない（このチャンクでは検出のみ）。
        var firstChunkMessages = decoder.Push(Ascii("<34>0123456789abcdef"));
        // 破棄対象の行の続きと、LF、そして次の正常な行。
        var secondChunkMessages = decoder.Push(Ascii("ghij\n<34>ok\n"));

        Assert.Empty(firstChunkMessages);
        Assert.Single(secondChunkMessages);
        Assert.Equal("<34>ok", Encoding.ASCII.GetString(secondChunkMessages[0]));
        Assert.Equal(1, decoder.OversizedMessagesDiscardedCount);
    }

    [Fact]
    public void Push_NonTransparent_MultipleOversizedLines_DiscardsEachIndependently()
    {
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 8 });
        var frame = Ascii("<34>0123456789\n<34>abcdefghij\n<34>ok\n");

        var messages = decoder.Push(frame);

        Assert.Single(messages);
        Assert.Equal("<34>ok", Encoding.ASCII.GetString(messages[0]));
        Assert.Equal(2, decoder.OversizedMessagesDiscardedCount);
    }

    [Fact]
    public void Flush_NonTransparent_DisconnectedWhileDiscardingOversizedLine_ReturnsNull()
    {
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 8 });
        decoder.Push(Ascii("<34>0123456789abcdef")); // LF がまだ来ておらず、破棄状態に入っている。

        var flushed = decoder.Flush();

        Assert.Null(flushed);
    }

    // ------------------------------------------------------------------
    // Flush（切断時の不完全データ。database.md §2.1）
    // ------------------------------------------------------------------

    [Fact]
    public void Flush_NonTransparent_NoPendingData_ReturnsNull()
    {
        var decoder = new TcpFrameDecoder();
        decoder.Push(Ascii("<34>hello\n"));

        var flushed = decoder.Flush();

        Assert.Null(flushed);
    }

    [Fact]
    public void Flush_NonTransparent_PendingDataWithoutTrailer_ReturnsPartial()
    {
        var decoder = new TcpFrameDecoder();
        decoder.Push(Ascii("<34>incomplete-tail"));

        var flushed = decoder.Flush();

        Assert.NotNull(flushed);
        Assert.Equal("<34>incomplete-tail", Encoding.ASCII.GetString(flushed!));
    }

    [Fact]
    public void Flush_OctetCounting_PendingBodyNotYetComplete_ReturnsPartial()
    {
        var decoder = new TcpFrameDecoder();
        decoder.Push(Ascii("20 <34>only-part-of-"));

        var flushed = decoder.Flush();

        Assert.NotNull(flushed);
        Assert.Equal("<34>only-part-of-", Encoding.ASCII.GetString(flushed!));
    }

    [Fact]
    public void Flush_OctetCounting_PendingLengthDigitsOnly_ReturnsPartial()
    {
        var decoder = new TcpFrameDecoder();
        decoder.Push(Ascii("12"));

        var flushed = decoder.Flush();

        Assert.NotNull(flushed);
        Assert.Equal("12", Encoding.ASCII.GetString(flushed!));
    }

    [Fact]
    public void HasPendingIncompleteData_AfterCompleteMessage_IsFalse()
    {
        var decoder = new TcpFrameDecoder();
        decoder.Push(Ascii("<34>hello\n"));

        Assert.False(decoder.HasPendingIncompleteData);
    }

    [Fact]
    public void HasPendingIncompleteData_WithBufferedPartialLine_IsTrue()
    {
        var decoder = new TcpFrameDecoder();
        decoder.Push(Ascii("<34>partial"));

        Assert.True(decoder.HasPendingIncompleteData);
    }
}
