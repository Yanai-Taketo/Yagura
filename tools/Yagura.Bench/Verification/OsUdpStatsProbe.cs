using System.Net.NetworkInformation;

namespace Yagura.Bench.Verification;

/// <summary>
/// OS レベル UDP 取りこぼしの観測（Issue #60「保存件数 + 全カウンタ + OS 統計の突合」。
/// architecture.md §4.2）。
/// </summary>
/// <remarks>
/// <para>
/// バーストシナリオ（多数のソケットから瞬時に大量送出する）では、アプリの Q1 に届く前に
/// OS の UDP 受信バッファ自体が溢れることがある——この破棄はアプリ内カウンタ
/// （<see cref="Yagura.Ingestion.Diagnostics.IngestionMetrics"/> の 7 種）のいずれにも現れない
/// （§4.1「サーバで観測できるのは OS バッファ以降のみ」）。architecture.md §4.2 は
/// <c>IPGlobalProperties.GetUdpIPv4Statistics()</c>/<c>GetUdpIPv6Statistics()</c> の
/// <c>IncomingDatagramsDiscarded</c> をこの観測手段として採用済み——本プローブは同じ API を
/// <b>ベンチプロセス自身から</b>読む（システム全体統計であり、プロセスを問わず同じ値が返る
/// ため、本体プロセスのインスタンスを介さずに直接読める。§4.2「システム全体統計であり…
/// 同居プロセスの破棄も含む」——ベンチはループバック専有を前提とするため、この前提の下では
/// 本体プロセスの破棄とみなしてよい）。
/// </para>
/// <para>
/// <b>ベンチでの必要性</b>: 本体のメタデータ領域（<see cref="Yagura.Host.Observability.MetadataStore"/>）は
/// この OS 統計ゲージを永続化しない（§4.3 のメタデータ領域はアプリ内カウンタ 7 種のみが対象。
/// OS 統計は「プロセス起動時からの差分」ゲージであり再起動をまたぐ累積という概念を持たないため
/// ——<see cref="Yagura.Ingestion.Diagnostics.IngestionCounterSnapshot"/> の doc コメント参照）。
/// そのためベンチは<b>実行開始前後で自ら差分を取る</b>必要がある——本体プロセス起動直後の
/// ベースライン値と、検証時点の値の差分を「本ベンチ実行中に発生した OS レベル破棄」とみなす。
/// </para>
/// </remarks>
public static class OsUdpStatsProbe
{
    /// <summary>現在の IPv4+IPv6 UDP 受信破棄の合計値を取得する。取得できない環境では 0 を返す。</summary>
    public static long GetCurrentTotalDiscarded()
    {
        long total = 0;

        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();

            try
            {
                total += properties.GetUdpIPv4Statistics().IncomingDatagramsDiscarded;
            }
            catch (NetworkInformationException)
            {
                // IPv4 統計が取得できない環境（§4.2「取得できない環境ではゲージ自体を登録しない」と
                // 同じ判断——ベンチも取得不可を 0 加算として扱い、突合の失敗ではなく「観測対象外」
                // として扱う）。
            }

            try
            {
                total += properties.GetUdpIPv6Statistics().IncomingDatagramsDiscarded;
            }
            catch (NetworkInformationException)
            {
                // 同上（IPv6）。
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return 0;
        }

        return total;
    }
}
