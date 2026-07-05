using System.Text;
using Yagura.Storage.Spool;

namespace Yagura.Storage.Tests.Spool;

/// <summary>
/// <see cref="Crc32"/> の自前実装が標準 CRC-32（IEEE 802.3。zlib 等が使う多項式）どおりで
/// あることを、公知の既知入出力ペアで確認する（DiskSpool.cs のコメント参照。
/// BCL に実装がなく、外部パッケージも追加しないための自前実装のため、実装ミスがないことを
/// テストで担保する）。
/// </summary>
public class Crc32Tests
{
    [Fact]
    public void Compute_EmptyInput_ReturnsZero()
    {
        // CRC-32 の定義上、空入力は 0 になる（多くの実装・言語標準ライブラリで確認できる
        // 既知の性質。初期値 0xFFFFFFFF を最終 XOR 0xFFFFFFFF で打ち消すため）。
        Assert.Equal(0u, Crc32.Compute([]));
    }

    [Fact]
    public void Compute_AsciiString123456789_MatchesWellKnownCheckValue()
    {
        // CRC-32/ISO-HDLC（zlib crc32 と同一多項式・反射・初期値・最終 XOR）の公式チェック値は
        // 入力 "123456789" に対して 0xCBF43926 であることが広く文書化されている
        // （CRC RevEng の catalogue "crc-32" エントリの check 値、および zlib crc32() の
        // 既知の相互運用結果として複数の独立実装で一致が確認されている値）。
        var input = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, Crc32.Compute(input));
    }

    [Fact]
    public void Compute_SameInputTwice_IsDeterministic()
    {
        var input = Encoding.UTF8.GetBytes("Yagura ディスクスプール");
        var first = Crc32.Compute(input);
        var second = Crc32.Compute(input);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_DifferentInputs_ProduceDifferentValues()
    {
        var a = Crc32.Compute(Encoding.UTF8.GetBytes("record-a"));
        var b = Crc32.Compute(Encoding.UTF8.GetBytes("record-b"));

        Assert.NotEqual(a, b);
    }
}
