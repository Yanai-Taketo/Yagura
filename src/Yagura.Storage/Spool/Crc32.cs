namespace Yagura.Storage.Spool;

/// <summary>
/// CRC-32（IEEE 802.3 多項式。zlib・gzip 等で使われる標準アルゴリズム）の自前実装。
/// </summary>
/// <remarks>
/// <para>
/// スプールのレコード単位破損検出（architecture.md §3.2.1「レコード単位の破損検出
/// （チェックサム）」）に使う。BCL には CRC-32 の実装がなく（<c>System.IO.Hashing.Crc32</c>
/// は別 NuGet パッケージであり BCL ではない。<c>dotnet package search System.IO.Hashing
/// --exact-match</c> で nuget.org 上の独立パッケージであることを確認済み、2026-07-05）、
/// 本機構は「追加パッケージは原則 BCL のみ」（依頼条件）のため、公知のアルゴリズムを
/// 自前実装する（外部依存として追加しない——ライブ検証や Vendored 記録は不要）。
/// </para>
/// <para>
/// 多項式 0xEDB88320（反転表現）はゲーム・通信規格等で広く使われる標準 CRC-32 であり、
/// 本用途では「実装が公知アルゴリズムどおりであること」をテスト（既知入出力ペアでの
/// 照合）で担保する。暗号学的強度は不要（改ざん対策ではなく偶発的破損の検出が目的）。
/// </para>
/// </remarks>
internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? (value >> 1) ^ Polynomial
                    : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    /// <summary>
    /// 指定バイト列の CRC-32 を計算する。
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            var index = (byte)(crc ^ b);
            crc = (crc >> 8) ^ Table[index];
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
