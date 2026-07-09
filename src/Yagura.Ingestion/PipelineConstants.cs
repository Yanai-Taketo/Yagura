namespace Yagura.Ingestion;

/// <summary>
/// パイプラインの暫定定数（architecture.md §9 実測待ち一覧 M-1・M-13）。
/// </summary>
/// <remarks>
/// ここに定義する値はすべて「実測で確定するまでの暫定値」である。ベンチハーネス
/// （architecture.md §5.1、M-1）による実測後、確定値を全体設計書へ転記し、
/// このクラスの値も合わせて更新する。
/// </remarks>
public static class PipelineConstants
{
    /// <summary>
    /// Q1（受信段 → 解析段、UDP 由来）の容量。
    /// architecture.md §3.1・M-1「Q1・Q2 の容量」の実測確定待ち。
    /// </summary>
    public const int Q1Capacity = 1024;

    /// <summary>
    /// Q2（解析段 → 永続化段）の容量。
    /// architecture.md §3.1・M-1「Q1・Q2 の容量」の実測確定待ち。
    /// </summary>
    public const int Q2Capacity = 1024;

    /// <summary>
    /// 永続化段のバッチ上限件数（N 件で書き込みを発行する）。
    /// architecture.md §2.1・M-1「書き込みバッチサイズ・間隔」の実測確定待ち。
    /// </summary>
    public const int WriteBatchMaxSize = 100;

    /// <summary>
    /// 永続化段のバッチ待機時間上限（T。この時間が経過したら件数未達でも書き込みを発行する）。
    /// architecture.md §2.1・M-1「書き込みバッチサイズ・間隔」の実測確定待ち。
    /// </summary>
    public static readonly TimeSpan WriteBatchMaxWait = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// バッチ書き込み 1 回のタイムアウト（応答のないハングを打ち切る）。
    /// architecture.md §2.1・§3.2.1・M-13「永続化書き込みのタイムアウト値」の実測確定待ち。
    /// </summary>
    public static readonly TimeSpan WriteBatchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <see cref="Yagura.Storage.LogStoreWriteGate"/>（Issue #151。ライブ・drain・保持期間削除の
    /// 3 経路を直列化する書き込みゲート）の取得を待つ時間上限。<b><see cref="WriteBatchTimeout"/>
    /// より意図的に大きくする</b>——ゲート取得の待ち時間を DB 操作そのもののタイムアウトと
    /// 同じ予算で縛ると、保持期間削除がゲートを長く保持している間、ライブ書き込みの毎バッチが
    /// 「ゲート待ちだけで」<see cref="WriteBatchTimeout"/> を使い切り、DB 自体は健全なのに
    /// 「速度不足」と誤認してスプール退避が連発する（database.md §3 が名指しするリスク。
    /// <see cref="Yagura.Storage.LogStoreWriteGate"/> の doc コメント参照）。この時間内にゲートを
    /// 取得できない場合、呼び出し元は既存のスプール退避（ライブ）・未消化のまま残す（drain）
    /// 経路へ、DB タイムアウトと区別できるログを添えて合流させる。実測確定待ちの暫定値
    /// （本クラスの他の値と同じ運用）。
    /// </summary>
    public static readonly TimeSpan WriteGateAcquireTimeout = TimeSpan.FromSeconds(30);
}
