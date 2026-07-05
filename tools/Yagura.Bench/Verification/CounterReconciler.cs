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
        long osUdpDatagramsDiscardedDelta = 0)
    {
        // architecture.md §4.1 の表: 破棄・退避系カウンタの合計。「スプール退避」は損失では
        // なく一時的な経由地だが、drain 完了後は savedCount 側に現れるため、drain 完了前に
        // 突合すると二重計上のように見える——呼び出し側は drain 完了（スプール使用量 0）を
        // 待ってから本メソッドを呼ぶこと（ScenarioRunner の待機ロジック参照）。
        var accountedLoss =
            counters.InternalBufferDropped +
            counters.TcpConnectionRejected +
            counters.SpoolWriteFailed +
            counters.SpoolDiscarded +
            counters.PersistenceFailed +
            counters.FlowControlDropped;

        // スプール退避（SpoolEvacuated）は「損失ではない予兆シグナル」（§4.1）であり、drain 完了後は
        // savedCount 側に含まれるため突合式には加えない。drain が完了していない状態（スプール
        // 使用量 > 0）で突合すると不一致になり得る——これは検証器の欠陥ではなく「まだ drain 中」
        // というシナリオの状態を表す。ReconciliationResult.SpoolEvacuatedCount で参考値として返す。
        //
        // OS レベル UDP 受信破棄（§4.2）は Q1 より手前（OS ソケットバッファ）での損失であり、
        // アプリ内カウンタのいずれにも現れない——architecture.md §3.1 の表の「OS ソケットバッファ」
        // 行は「OS 統計との突合」を計上先とする、と明記されている。本ベンチはこの手段（§4.2 M-8）
        // をそのまま使い、突合式へ組み込む。
        var expectedTotal = savedCount + accountedLoss + osUdpDatagramsDiscardedDelta;
        var difference = sentCount - expectedTotal;

        return new ReconciliationResult(
            SentCount: sentCount,
            SavedCount: savedCount,
            AccountedLossCount: accountedLoss,
            SpoolEvacuatedCount: counters.SpoolEvacuated,
            OsUdpDatagramsDiscardedDelta: osUdpDatagramsDiscardedDelta,
            Difference: difference,
            IsReconciled: difference == 0,
            Counters: counters);
    }
}

/// <summary>突合結果。</summary>
/// <param name="SentCount">送信数（負荷生成器の自己計上値）。</param>
/// <param name="SavedCount">DB provider の保存件数（実行前後の差分）。</param>
/// <param name="AccountedLossCount">破棄・退避系カウンタ（スプール退避を除く）の合計。</param>
/// <param name="SpoolEvacuatedCount">参考値: スプール退避カウンタ（drain 完了後は SavedCount に含まれる）。</param>
/// <param name="OsUdpDatagramsDiscardedDelta">OS レベル UDP 受信破棄の実行前後差分（§4.2）。</param>
/// <param name="Difference">送信数 - (保存件数 + カウンタ合計 + OS統計差分)。0 であれば「損失は必ずどれかのカウンタに計上される」の検証成立。</param>
/// <param name="IsReconciled"><see cref="Difference"/> が 0 かどうか。</param>
/// <param name="Counters">生のカウンタスナップショット（レポート出力用）。</param>
public sealed record ReconciliationResult(
    long SentCount,
    long SavedCount,
    long AccountedLossCount,
    long SpoolEvacuatedCount,
    long OsUdpDatagramsDiscardedDelta,
    long Difference,
    bool IsReconciled,
    IngestionCounterSnapshot Counters);
