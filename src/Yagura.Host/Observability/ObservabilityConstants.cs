namespace Yagura.Host.Observability;

/// <summary>
/// メタデータ領域・受信断可視化の暫定定数（architecture.md §9 実測待ち一覧 M-11）。
/// </summary>
/// <remarks>
/// <see cref="Yagura.Ingestion.PipelineConstants"/> と同じ運用——ここに定義する値はすべて
/// 「実測で確定するまでの暫定値」であり、ベンチハーネス実測後に確定値を全体設計書へ転記し、
/// このクラスの値も合わせて更新する。
/// </remarks>
public static class ObservabilityConstants
{
    /// <summary>
    /// カウンタ永続化間隔・生存時刻更新間隔（architecture.md §4.3・§4.4・M-11「仮値: 10 秒」）。
    /// 本実装では「同一周期」を採用した（M-11 が「同一周期か個別かを含む」を確定待ちとしている
    /// 論点に対する実装時の判断——カウンタ・生存時刻はどちらも「一定間隔の定期永続化」という
    /// 同じ目的（クラッシュ時の損失窓の限定）を持ち、同じメタデータファイルへの単一の書き込み
    /// 操作にまとめられるため、書き込み回数・ディスク I/O を増やす個別周期を採る理由がない）。
    /// </summary>
    public static readonly TimeSpan MetadataPersistInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// システムイベントの Kind: 受信断（正常停止起因）。database.md §2.3。
    /// M8-3 で値の正を <see cref="Yagura.Storage.SystemEventKinds"/>（横断契約側）へ移した
    /// ——閲覧 UI が同じ値を参照するため（同クラスの remarks 参照）。本定数は既存参照の互換用の別名。
    /// </summary>
    public const string SystemEventKindDowntimeNormalStop = Yagura.Storage.SystemEventKinds.DowntimeNormalStop;

    /// <summary>
    /// システムイベントの Kind: 受信断（クラッシュ近似断点）。database.md §2.3。
    /// （値の正は <see cref="Yagura.Storage.SystemEventKinds"/>——上と同じ理由の別名。）
    /// </summary>
    public const string SystemEventKindDowntimeCrashApproximate = Yagura.Storage.SystemEventKinds.DowntimeCrashApproximate;
}
