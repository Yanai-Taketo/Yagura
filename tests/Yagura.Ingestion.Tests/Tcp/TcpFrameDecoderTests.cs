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
    public void Push_OctetCounting_MessageLengthExceedsMax_ThrowsFrameSizeExceeded()
    {
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 10 });

        Assert.Throws<TcpFrameSizeExceededException>(() => decoder.Push(Ascii("100 ")));
    }

    [Fact]
    public void Push_OctetCounting_BodyExceedsMaxAcrossChunks_ThrowsBeforeUnboundedGrowth()
    {
        // MSG-LEN 自体は上限以下だが、本体を送り切る前に別の経路で上限超過を検出できること
        // は MSG-LEN 判定時点で完結するため、ここでは MSG-LEN 超過が「本体を受け取り切る前」
        // （最初のチャンクの時点）で例外化されることを確認する（無制限のバッファ確保を防ぐ）。
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 5 });

        Assert.Throws<TcpFrameSizeExceededException>(() => decoder.Push(Ascii("6 ")));
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

    [Fact]
    public void Push_NonTransparent_MessageExceedsMaxWithoutLf_ThrowsFrameSizeExceeded()
    {
        var decoder = new TcpFrameDecoder(new TcpFrameDecoderOptions { MaxMessageLength = 8 });

        // 先頭が数字以外（non-transparent-framing 判別）で、LF が来ないまま上限を超える
        // ストリームへの防御（M4-1 依頼の安全側判断）。
        Assert.Throws<TcpFrameSizeExceededException>(() => decoder.Push(Ascii("<34>123456789")));
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
