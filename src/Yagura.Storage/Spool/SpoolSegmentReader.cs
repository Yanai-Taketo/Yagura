using System.Buffers.Binary;

namespace Yagura.Storage.Spool;

/// <summary>
/// セグメントファイル 1 本を先頭から読み、正常なフレームを列挙する
/// （architecture.md §3.2.1「レコード単位の破損検出」）。
/// </summary>
/// <remarks>
/// <b>破損末尾の検出方法</b>: クラッシュ・強制終了で発生し得る破損は「追記の途中で
/// ファイルが終端する」形に限られる（追記専用のため、既存の完全なフレームが後から
/// 書き換わることはない）。読み取り中に次のいずれかが起きた時点で、以降のバイト列を
/// 「破損した末尾」とみなして読み取りを打ち切る（例外を投げず、それまでに読めた
/// レコードは全件返す）:
/// <list type="bullet">
/// <item>長さプレフィックス 4 バイトを読み切れない（ファイルがそこで終わっている）</item>
/// <item>長さプレフィックスが負値、または残りバイト数（payload + CRC 分）に満たない
/// （payload 書き込みの途中で切れている）</item>
/// <item>CRC-32 が一致しない（torn write によるビット化けを検出。長さは読めても
/// 中身が破損している稀なケース）</item>
/// </list>
/// いずれの場合も「それ以降のフレームの境界は保証されない」ため、再同期（次の
/// フレーム境界を探す）は試みない——追記専用ファイルでは壊れるのは末尾のみという
/// 前提から、末尾を切り捨てる以上の回収は必要ない（かつ誤って中身をフレームの
/// 先頭と誤認するリスクを避けられる）。
/// </remarks>
internal static class SpoolSegmentReader
{
    /// <summary>
    /// セグメントファイルを読み、正常なフレームを <see cref="SpoolRecord"/> として列挙する。
    /// </summary>
    /// <param name="filePath">セグメントファイルのパス。</param>
    /// <param name="corruptTailDetected">破損した末尾を検出し、読み取りを打ち切った場合に <c>true</c>。</param>
    /// <param name="corruptTailBytes">
    /// 破損した末尾として読み捨てたバイト数（<paramref name="corruptTailDetected"/> が
    /// <c>false</c> の場合は常に 0）。破損した末尾はフレーム境界が保証されないため
    /// レコード単位では数えられない（クラス remarks 参照）——バイト数を計上の単位とする
    /// （Issue #201: architecture.md §3.1「カウンタに計上されない喪失は重大」への対応）。
    /// </param>
    public static List<SpoolRecord> ReadValidRecords(string filePath, out bool corruptTailDetected, out long corruptTailBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var records = new List<SpoolRecord>();
        corruptTailDetected = false;
        corruptTailBytes = 0;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Span<byte> lengthPrefix = stackalloc byte[4];

        while (true)
        {
            // このフレームの読み取りを試みる直前の位置——破損と判定した場合、ここから
            // ファイル末尾までを「読み捨てた破損バイト数」として計上する。
            var frameStartPosition = stream.Position;

            var lengthBytesRead = ReadFully(stream, lengthPrefix);
            if (lengthBytesRead == 0)
            {
                // ちょうどファイル末尾——正常終了（このセグメントに破損はない）。
                break;
            }

            if (lengthBytesRead < 4)
            {
                // 長さプレフィックスの途中でファイルが終わっている——破損した末尾。
                corruptTailDetected = true;
                corruptTailBytes = stream.Length - frameStartPosition;
                break;
            }

            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);

            // 割り当ての前に長さを検証する。破損した長さプレフィックス(torn write の
            // 途中バイト)は任意の値を取り得るため、負値だけでなく「ファイルの残りバイト数
            // (payload + CRC 4 バイト)を超える値」もここで破損と判定する——検証せずに
            // new byte[payloadLength + 4] すると、巨大値では確保自体が失敗し(int 上限
            // 近傍では負値配列例外)、破損末尾の切り捨てという正常経路を通れない。
            var remainingBytes = stream.Length - stream.Position;
            if (payloadLength < 0 || (long)payloadLength + 4 > remainingBytes)
            {
                corruptTailDetected = true;
                corruptTailBytes = stream.Length - frameStartPosition;
                break;
            }

            var frameRemainder = new byte[payloadLength + 4]; // payload + CRC
            var frameBytesRead = ReadFully(stream, frameRemainder);
            if (frameBytesRead < frameRemainder.Length)
            {
                // payload または CRC の途中でファイルが終わっている——破損した末尾。
                corruptTailDetected = true;
                corruptTailBytes = stream.Length - frameStartPosition;
                break;
            }

            var payload = frameRemainder.AsSpan(0, payloadLength).ToArray();
            var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(frameRemainder.AsSpan(payloadLength, 4));
            var actualCrc = Crc32.Compute(payload);

            if (storedCrc != actualCrc)
            {
                // 長さは読めたが中身が破損している（torn write）——以降の境界は
                // 保証されないため、ここで打ち切る。
                corruptTailDetected = true;
                corruptTailBytes = stream.Length - frameStartPosition;
                break;
            }

            records.Add(SpoolRecordSerializer.DeserializePayload(payload));
        }

        return records;
    }

    /// <summary>
    /// <paramref name="buffer"/> を可能な限り埋める（複数回の読み取りが必要な場合に対応）。
    /// 返り値は実際に読めたバイト数（ファイル終端で <paramref name="buffer"/> より少ないことがある）。
    /// </summary>
    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer[totalRead..]);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
