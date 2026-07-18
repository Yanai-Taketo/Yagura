using System.Net.NetworkInformation;

namespace Yagura.Bench.Verification;

/// <summary>
/// OS の UDP 統計カウンタの参考観測（Issue #60。**突合式には使わない**——下記の実機検証結果参照）。
/// </summary>
/// <remarks>
/// <para>
/// <b>当初の想定と実機検証で判明した限界（M7-2。2026-07-05 実機確認。2026-07-18 に理由を訂正）</b>:
/// 当初は architecture.md §4.2 の <c>IncomingDatagramsDiscarded</c> を「OS ソケットバッファでの
/// 破棄」の観測手段として突合式へ組み込んでいた。しかし実測で、OS バッファ破棄が実際に発生して
/// いる状況（バースト実測: 送信 2000・アプリ受信 1006・Q1 破棄 0）でも
/// <c>IncomingDatagramsDiscarded</c> の増分が 0 であることが判明した。
/// </para>
/// <para>
/// <b>理由の訂正（2026-07-18。ADR-0016 改訂履歴 1）</b>: 当初これを「Windows は自己宛 UDP を統計の
/// カウント経路の外で配送する（バイパスする）」と説明していたが、これは**誤りであり撤回する**。
/// 実際には自己宛でも受信総数は正確に計上される（Windows Server 2025 / .NET 10 実測で送信数と
/// 完全一致）。当時「受信総数の増分も 0」と観測されたのは、検証スクリプトが存在しないプロパティ名
/// <c>IncomingDatagrams</c> を読んでいた測定アーティファクトである（正しくは
/// <c>DatagramsReceived</c>。PowerShell は存在しないプロパティを黙って <c>$null</c> と返す）。
/// 真の理由は構造的なものである——<c>IncomingDatagramsDiscarded</c> は <c>netstat -s -p udp</c> の
/// <c>No Ports</c>（listener 不在による破棄）に対応しており、Windows の UDP MIB には**ソケット受信
/// バッファ満杯による破棄のカウンタが存在しない**。したがって本統計は送信元の位置によらず
/// バッファ破棄の観測には使えず、突合式に入れると他プロセス由来の背景ノイズだけを取り込む。
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
/// （受信 = YAGURA-STG。溢れ確実の 100,000 通で破棄系増分 0——一次資料:
/// results/2026-07-05-cross-machine-udp-stats/）で**外部送信元のトラフィックの破棄すら計上
/// されない**ことが確定し、ADR-0016 決定 3 で製品コードから撤去された。
/// 本 probe は <c>IPGlobalProperties</c> を直接読む独立実装であり製品コードに依存しない。
/// <b>再評価トリガ (d) は実施済み（2026-07-18。ADR-0016 改訂履歴 1）</b>——Windows Server 2025 /
/// .NET 10 実機で「破棄の観測手段としては機能しない」が確定し、恒久受容へ。一次資料:
/// results/2026-07-18-server-udp-stats-trigger-d/
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
