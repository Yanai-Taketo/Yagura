namespace Yagura.Ingestion.Diagnostics;

/// <summary>
/// <see cref="IngestionMetrics"/> のカウンタ累積値のスナップショット（architecture.md §4.3
/// メタデータ領域への永続化・起動時の引き継ぎに使う）。
/// </summary>
/// <remarks>
/// プロパティ名は <see cref="IngestionMetrics"/> の計器名（<c>yagura.ingestion.*</c>）と
/// 1 対 1 対応する（ui.md「画面の言葉 1 つは開発用語 1 つに一意対応させる」の要件を、
/// メタデータ領域の内部表現の時点から満たすため）。OS 統計突合ゲージ（§4.2）はプロセス起動時
/// からの差分として観測するため対象外（再起動をまたぐ累積という概念を持たない）。
/// </remarks>
/// <param name="InternalBufferDropped">内部バッファ破棄の累積値。</param>
/// <param name="TcpConnectionRejected">TCP 接続拒否の累積値。</param>
/// <param name="SpoolEvacuated">スプール退避の累積値。</param>
/// <param name="SpoolWriteFailed">スプール書込失敗の累積値。</param>
/// <param name="SpoolDiscarded">スプール破棄の累積値。</param>
/// <param name="PersistenceFailed">永続化失敗の累積値。</param>
/// <param name="FlowControlDropped">流量制御破棄の累積値。</param>
public sealed record IngestionCounterSnapshot(
    long InternalBufferDropped,
    long TcpConnectionRejected,
    long SpoolEvacuated,
    long SpoolWriteFailed,
    long SpoolDiscarded,
    long PersistenceFailed,
    long FlowControlDropped)
{
    /// <summary>全カウンタが 0 の初期スナップショット（メタデータ領域が無い初回起動用）。</summary>
    public static IngestionCounterSnapshot Zero { get; } = new(0, 0, 0, 0, 0, 0, 0);
}
