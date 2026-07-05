namespace Yagura.Storage.Spool;

/// <summary>
/// ディスクスプールの暫定定数（architecture.md §9 実測待ち一覧 M-12・M-3）。
/// </summary>
/// <remarks>
/// ここに定義する値はすべて「実測で確定するまでの暫定値」である
/// （<see cref="Yagura.Ingestion.PipelineConstants"/> と同じ運用）。ベンチハーネス
/// （architecture.md §5.1）による実測後、確定値を全体設計書へ転記し、このクラスの
/// 値も合わせて更新する。
/// </remarks>
public static class SpoolConstants
{
    /// <summary>
    /// スプールのディスク使用量上限（既定値。M-12 実測確定待ち）。
    /// 暫定値: 1 GiB（開発機の空き容量を大きく圧迫せず、かつバースト退避を
    /// 数時間分は吸収できる大きさとして安全側に選んだ値。実測未実施）。
    /// </summary>
    public const long DefaultQuotaBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// 1 セグメントファイルの目標最大サイズ（この値を超えたら現在のセグメントを
    /// 封止し新しいセグメントを開始する。drain・削除の粒度をセグメント単位にする
    /// ための暫定分割点であり、architecture.md に明記された数値ではない実装判断）。
    /// 暫定値: 4 MiB。
    /// </summary>
    public const long TargetSegmentSizeBytes = 4L * 1024 * 1024;

    /// <summary>
    /// スプール書き込み失敗時のリトライ回数（暫定値。実測確定待ち）。
    /// </summary>
    public const int WriteRetryCount = 3;

    /// <summary>
    /// スプール書き込みリトライ間隔（暫定値。実測確定待ち）。
    /// </summary>
    public static readonly TimeSpan WriteRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// drain 低水位（Q2 使用率がこれを下回っている間だけ drain を進める。M-3 実測確定待ち）。
    /// architecture.md §3.2.2「ライブ優先」のヒステリシス下限。
    /// </summary>
    public const double DrainLowWatermarkRatio = 0.2;

    /// <summary>
    /// drain 再開水位（Q2 使用率がこれを上回ったら drain を停止する。M-3 実測確定待ち）。
    /// 低水位と異なる値にすることでヒステリシスを持たせ、水位付近での drain の
    /// 起動・停止の振動を防ぐ。
    /// </summary>
    public const double DrainHighWatermarkRatio = 0.5;

    /// <summary>
    /// drain 1 バッチあたりの投入件数上限（速度上限。M-3 実測確定待ち）。
    /// </summary>
    public const int DrainBatchMaxSize = 100;

    /// <summary>
    /// 保存先障害中に drain を止めてから再試行するまでのバックオフ時間（M-3 実測確定待ち）。
    /// </summary>
    public static readonly TimeSpan DrainBackoffDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// drain がライブ流入と衝突しないか確認するポーリング間隔（実装判断。architecture.md に
    /// 明記された数値ではない）。
    /// </summary>
    public static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(200);
}
