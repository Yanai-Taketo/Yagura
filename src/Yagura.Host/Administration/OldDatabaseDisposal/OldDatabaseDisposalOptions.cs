namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>
/// 処分の実行環境（合成ルートが結線する）。
/// </summary>
/// <param name="SpoolDirectory">一時保管（スプール）の置き場所（無効なら <see langword="null"/>）。保管先の禁止範囲と空き容量の下限の判定に使う。</param>
/// <param name="InstallDirectory">プログラムのインストール先（保管先の禁止範囲）。</param>
/// <param name="SpoolRemainingQuotaBytes">一時保管の使用量上限の残り枠（無効なら <see langword="null"/>）。</param>
/// <param name="IsLiveIngestionUnderPressure">受信が逼迫しているか（一時保管に未消化のデータがある・保存待ちが高水位）。</param>
/// <param name="RetentionDays">現在のログ保存期間（日。無期限は <see langword="null"/>）。</param>
public sealed record OldDatabaseDisposalEnvironment(
    string? SpoolDirectory,
    string InstallDirectory,
    Func<long?> SpoolRemainingQuotaBytes,
    Func<bool> IsLiveIngestionUnderPressure,
    Func<int?> RetentionDays);

/// <summary>処分の仮値（database.md §6.3。テストで短くできるよう差し替え可能にする）。</summary>
public sealed record OldDatabaseDisposalOptions
{
    /// <summary>既定値。</summary>
    public static OldDatabaseDisposalOptions Default { get; } = new();

    /// <summary>
    /// 空き容量の下限（仮値 1 GiB）。空き容量監視の警告閾値（1006）と同じ値を使う——保管で使い切って
    /// 警告が鳴り続ける状態を自分で作らないため。
    /// </summary>
    public long FreeSpaceFloorBytes { get; init; } = Yagura.Host.Observability.ActiveNotification.ActiveNotificationConstants.MonitoredVolumeFreeSpaceMinBytes;

    /// <summary>削除の共有違反の再試行の間隔（仮値 1 秒）。</summary>
    public TimeSpan DeleteRetryInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 削除の共有違反を再試行する合計時間（仮値 30 秒）。ウイルス対策・バックアップソフトが一瞬開いただけの
    /// 「部分失敗」を減らしつつ、本当に使用中のときに利用者を長く待たせない長さ。
    /// </summary>
    public TimeSpan DeleteRetryBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>書き出し・検証の途中で空き容量と受信の逼迫を再検査する間隔（仮値 1 秒）。</summary>
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// サービスの停止要求から処分の取り消しの完了を待つ上限（仮値 5 秒）。停止手順（メモリ上のログの一時保管への
    /// 退避）を待たせないための上限であり、Windows のサービス停止の待機時間より十分短く取る。
    /// </summary>
    public TimeSpan StopResponseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>確認材料の読み取り・処分の開始が、旧ファイルの排他を待つ上限。</summary>
    public TimeSpan GateWait { get; init; } = TimeSpan.FromSeconds(5);
}
