using Yagura.Host.Observability;
using Yagura.Ingestion.Diagnostics;
using Yagura.Storage;

namespace Yagura.Bench.Verification;

/// <summary>
/// 「送信数 = 保存件数 + 全カウンタ（破棄・退避系）の合計」の突合（Issue #60。architecture.md
/// §4.1「損失は必ずどれかのカウンタに計上される」の検証がそのまま合否条件になる）。
/// </summary>
/// <remarks>
/// <para>
/// <b>カウンタの取得方式（調査結果）</b>: 本体プロセスの <c>Meter("Yagura")</c> を外部プロセスから
/// 直接購読する標準手段は存在しない（HTTP メトリクスエンドポイント・OpenTelemetry エクスポータの
/// 類は本リポジトリに未実装——src/Yagura.Web 配下・Program.cs を確認したが該当なし）。
/// <see cref="Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector{T}"/>
/// （既存の単体テストが使う方式）は同一プロセス内の <c>MeterListener</c> 購読が前提であり、
/// ベンチが Yagura.Host を子プロセスとして起動する構成（tests/Yagura.E2E.Tests と同じ実バイナリ
/// 起動）とは相容れない。
/// </para>
/// <para>
/// <b>採用した方式</b>: architecture.md §4.3 の<b>メタデータ領域</b>
/// （<c>observability-state.json</c>。<see cref="MetadataStore"/>）を読む。この領域はカウンタ
/// 累積値を「一定間隔（既定 10 秒）+ 正常停止時」にプロセス外のローカル JSON ファイルへ原子的に
/// 書き出す設計であり（M4-4 で実装済み）、外部プロセスからの読み取り専用アクセスに理想的に
/// 適合する（DB provider に依存しないためスプール発動シナリオでも読める）。<b>採用理由</b>:
/// (i) 追加実装が不要（既存の永続化機構をそのまま利用）、(ii) in-process ホスティングの代替案
/// （ベンチプロセス内で IngestionPipeline を直接インスタンス化する）は「実バイナリでの検証」
/// という本ハーネスの目的（tests/Yagura.E2E.Tests と同じ設計意図）から外れるため見送った、
/// (iii) HTTP メトリクスエンドポイントの新規実装は本 Issue のスコープ外（製品機能の追加になる）。
/// <b>正確な最終値を得るための前提</b>: メタデータ領域の停止手順 3（最終カウンタ書き込み）が
/// 実行されるには本体プロセスの<b>グレースフル停止</b>が必要であり、単純な <c>Kill</c> では
/// 直近の定期永続化以降の増分が失われ、実機検証で突合が不成立になった。そのため
/// <see cref="Yagura.Bench.HostProcess.BenchHostProcess.StopGracefullyAsync"/>（内部で
/// <see cref="Yagura.Bench.HostProcess.ConsoleCtrlSender"/> を使い Ctrl+C 相当のコンソール
/// シグナルを送出する）を実装し、これを標準の停止経路とした——tests/Yagura.E2E.Tests の smoke
/// テストが Kill で足りると判断していたのとは異なる要求（突合の数値的正確性）があるため。
/// </para>
/// </remarks>
public static class CounterReconciler
{
    /// <summary>
    /// メタデータ領域から現在のカウンタ累積値を読み取る。
    /// </summary>
    public static IngestionCounterSnapshot ReadCounters(string dataRoot) =>
        MetadataStore.Read(dataRoot, logger: null).Counters;

    /// <summary>
    /// 突合を実行する。<paramref name="sentCount"/>（送信数）と
    /// <paramref name="savedCount"/> + カウンタ合計 + OS 統計差分が一致するかを判定する
    /// （Issue #60「送信数 = 保存件数 + 全カウンタ + OS 統計の突合」）。
    /// </summary>
    /// <param name="sentCount">負荷生成器が計上した送信成功数（<see cref="LoadGeneration.LoadGeneratorResult.SentCount"/>）。</param>
    /// <param name="savedCount">
    /// DB provider の保存件数（<see cref="ILogStore.GetStatisticsAsync"/> の <c>RecordCount</c>）。
    /// ベンチ実行前の既存件数がある場合は、呼び出し側が実行前後の差分を渡すこと（同一 DB を
    /// 使い回すシナリオでの誤差防止）。
    /// </param>
    /// <param name="counters">メタデータ領域から読んだカウンタ累積値（実行前後の差分）。</param>
    /// <param name="osUdpDatagramsDiscardedDelta">
    /// <see cref="OsUdpStatsProbe"/> で取得した OS レベル UDP 受信破棄の実行前後差分
    /// （architecture.md §4.2。アプリの Q1 に届く前に OS バッファで破棄された数——バースト
    /// シナリオでは非ゼロになり得る。TCP シナリオ・OS 統計取得不可環境では常に 0 を渡してよい）。
    /// </param>
    public static ReconciliationResult Reconcile(
        long sentCount,
        long savedCount,
        IngestionCounterSnapshot counters,
        long osUdpDatagramsDiscardedDelta = 0,
        bool transportIsUdp = true)
    {
        // architecture.md §4.1 の表: 破棄・退避系カウンタの合計。「スプール退避」は損失では
        // なく一時的な経由地だが、drain 完了後は savedCount 側に現れるため、drain 完了前に
        // 突合すると二重計上のように見える——呼び出し側は drain 完了（スプール使用量 0）を
        // 待ってから本メソッドを呼ぶこと（ScenarioRunner の待機ロジック参照）。
        //
        // Issue #143 で追加した「TCP メッセージ破棄（上限超過）」は 1 メッセージ単位の実損失
        // （§4.1 の意味で InternalBufferDropped 等と同じ「送信されたが保存されなかった」事象）
        // のため合計へ含める。同時に追加した「TCP 接続断」「TCP 接続アイドルタイムアウト」、
        // およびオーナー決定 2026-07-09 で追加した「TCP 接続再同期上限」「TCP フレーミング
        // 進捗タイムアウト」は、メッセージ単位ではなく接続単位の事象（切断そのものは損失では
        // ない——§4.5「損失ではなく解釈の手がかり」）のため、本メッセージ数ベースの突合式には
        // 含めない。
        var accountedLoss =
            counters.InternalBufferDropped +
            counters.TcpConnectionRejected +
            counters.SpoolWriteFailed +
            counters.SpoolDiscarded +
            counters.PersistenceFailed +
            counters.FlowControlDropped +
            counters.TcpMessageOversizedDiscarded;

        // スプール退避（SpoolEvacuated）は「損失ではない予兆シグナル」（§4.1）であり、drain 完了後は
        // savedCount 側に含まれるため突合式には加えない。drain が完了していない状態（スプール
        // 使用量 > 0）で突合すると不一致になり得る——これは検証器の欠陥ではなく「まだ drain 中」
        // というシナリオの状態を表す。ReconciliationResult.SpoolEvacuatedCount で参考値として返す。
        //
        // OS レベル UDP 受信破棄は突合式に「含めない」（M7-2 実機検証 2026-07-05 での設計変更）:
        // Windows は自己宛 UDP（loopback・自ホスト IP 宛）を UDP 統計のカウント経路の外で配送する
        // ——バースト実測で約 1000 件の OS バッファ破棄が発生している状況下でも
        // IncomingDatagrams（受信総数）・IncomingDatagramsDiscarded・IncomingDatagramsWithErrors の
        // いずれも増分 0 であることを実機で確認した（アプリは同時刻に 1000 件超を受信済み =
        // 配送自体は行われている）。対照として自ホストの LAN IP 宛でも増分 0（自己宛はアドレスに
        // よらず同じバイパス経路）。つまり自己宛送信であるベンチのトラフィックはこの統計に
        // 原理的に現れず、式へ入れると他プロセス由来の背景ノイズ（実測で +1 の混入を観測）だけを
        // 取り込むことになる。<see cref="ReconciliationResult.OsUdpDatagramsDiscardedDelta"/> は
        // 参考値として保持するが、期待値の計算には使わない。
        //
        // 代わりに、ベンチは閉じた系（送信数は自前の正確な計上・OS バッファ以降は全カウンタで
        // 観測済み）であることを利用し、UDP の正の差分を「OS ソケットバッファでの破棄（導出値）」
        // と解釈する——読めないカウンタの代わりに引き算そのものが観測手段になる。
        // TCP はストリーム到達後の損失が必ずアプリ内カウンタに現れる設計のため、正の差分も
        // 常に不成立（計上漏れバグの疑い）として扱う。負の差分（過剰計上）はトランスポートに
        // よらず不成立。
        var expectedTotal = savedCount + accountedLoss;
        var difference = sentCount - expectedTotal;

        var derivedOsBufferLoss = transportIsUdp && difference > 0 ? difference : 0;
        var isReconciled = difference == 0 || derivedOsBufferLoss > 0;

        return new ReconciliationResult(
            SentCount: sentCount,
            SavedCount: savedCount,
            AccountedLossCount: accountedLoss,
            SpoolEvacuatedCount: counters.SpoolEvacuated,
            OsUdpDatagramsDiscardedDelta: osUdpDatagramsDiscardedDelta,
            DerivedOsBufferLossCount: derivedOsBufferLoss,
            Difference: difference,
            IsReconciled: isReconciled,
            Counters: counters);
    }
}

/// <summary>突合結果。</summary>
/// <param name="SentCount">送信数（負荷生成器の自己計上値）。</param>
/// <param name="SavedCount">DB provider の保存件数（実行前後の差分）。</param>
/// <param name="AccountedLossCount">破棄・退避系カウンタ（スプール退避・TCP 接続断・TCP 接続
/// アイドルタイムアウトを除く、メッセージ単位の損失カウンタ）の合計。</param>
/// <param name="SpoolEvacuatedCount">参考値: スプール退避カウンタ（drain 完了後は SavedCount に含まれる）。</param>
/// <param name="OsUdpDatagramsDiscardedDelta">参考値: OS レベル UDP 受信破棄カウンタの実行前後差分。
/// 自己宛送信のベンチトラフィックはこの統計に現れない（実機確認 2026-07-05。<see cref="CounterReconciler.Reconcile"/>
/// のコメント参照）ため期待値の計算には使わない——非ゼロは他プロセス由来の背景ノイズを示す。</param>
/// <param name="DerivedOsBufferLossCount">OS ソケットバッファでの破棄の導出値（UDP のみ。送信数 -
/// 保存件数 - アプリ内カウンタ合計 の正の残差。閉じた系の引き算による観測——OS 統計カウンタが
/// 自己宛トラフィックに対して盲目のための代替手段）。</param>
/// <param name="Difference">送信数 - (保存件数 + アプリ内カウンタ合計)。</param>
/// <param name="IsReconciled">全損失がアプリ内カウンタまたは OS バッファ破棄（導出）に帰属できたか。
/// 負の差分（過剰計上）と TCP の正の差分（計上漏れ疑い）は false。</param>
/// <param name="Counters">生のカウンタスナップショット（レポート出力用）。</param>
public sealed record ReconciliationResult(
    long SentCount,
    long SavedCount,
    long AccountedLossCount,
    long SpoolEvacuatedCount,
    long OsUdpDatagramsDiscardedDelta,
    long DerivedOsBufferLossCount,
    long Difference,
    bool IsReconciled,
    IngestionCounterSnapshot Counters);
