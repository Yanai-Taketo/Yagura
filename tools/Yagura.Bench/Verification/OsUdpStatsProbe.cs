using System.Net.NetworkInformation;

namespace Yagura.Bench.Verification;

/// <summary>
/// OS の UDP 統計カウンタの参考観測（Issue #60。**突合式には使わない**——下記の実機検証結果参照）。
/// </summary>
/// <remarks>
/// <para>
/// <b>当初の想定と実機検証で判明した限界（M7-2。2026-07-05 実機確認）</b>: 当初は architecture.md
/// §4.2 の <c>IncomingDatagramsDiscarded</c> を「OS ソケットバッファでの破棄」の観測手段として
/// 突合式へ組み込んでいた。しかし実測で、**Windows は自己宛 UDP（loopback 宛・自ホストの実 IP 宛の
/// いずれも）をこの統計のカウント経路の外で配送する**ことが判明した——バースト実測で約 1000 件の
/// OS バッファ破棄が発生している状況（送信 2000・アプリ受信 1006・Q1 破棄 0）でも
/// <c>IncomingDatagrams</c>（受信総数）を含む UDP 統計全カウンタの増分が 0 だった。対照実験として
/// 自ホストの LAN IP 宛送信 100 通でも増分 0（アドレスによらず自己宛は同じバイパス経路）。
/// このため自己宛送信であるベンチのトラフィックには本統計は原理的に使えず、突合式に入れると
/// 他プロセス由来の背景ノイズ（実測で +1 の混入を観測）だけを取り込む。
/// </para>
/// <para>
/// <b>現在の役割</b>: 参考値の記録のみ（非ゼロ = ベンチ実行中に外部由来トラフィックの破棄が
/// 起きた、の意味）。OS ソケットバッファでの破棄そのものは、閉じた系の引き算
/// （<see cref="CounterReconciler"/> の <c>DerivedOsBufferLossCount</c>——送信数はベンチ自前の
/// 正確な計上、OS バッファ以降は全カウンタで観測済みのため、残差が OS バッファ破棄に一意に
/// 帰属できる）で導出する。
/// </para>
/// <para>
/// <b>製品側ゲージとの関係（ADR-0016）</b>: 当初「外部送信元からの実運用トラフィックには有効」と
/// 想定していた製品側のゲージ（旧 yagura.os.udp.*）は、その後の M7-2 クロスマシン実機検証
/// （受信 = YAGURA-STG。外部からの配送 8,601 通で受信総数増分 0・溢れ確実の 100,000 通で
/// 破棄系増分 0——一次資料: results/2026-07-05-cross-machine-udp-stats/）で**外部送信元の
/// トラフィックすら計上されない**ことが確定し、ADR-0016 決定 3 で製品コードから撤去された。
/// 本 probe は <c>IPGlobalProperties</c> を直接読む独立実装であり製品コードに依存しない——
/// ADR-0016 再評価トリガ (d)（Windows Server での本 API 動作確認）の実測は、同結果ディレクトリ
/// 同梱の <c>stats-check.ps1</c>（受信総数側の観測を含む）または本 probe の拡張で行う
/// （判定手順の成立条件は ADR-0016 チェックリスト⑦）。
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
                // IPv4 統計が取得できない環境。ベンチは取得不可を 0 加算として扱い、突合の
                // 失敗ではなく「観測対象外」として扱う（撤去前の製品ゲージが採っていた
                // 「取得できない環境では登録しない」と同趣旨の正直な縮退）。
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
