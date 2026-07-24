namespace Yagura.Host.Observability.ActiveNotification;

/// <summary>
/// 能動通知の背景監視（<see cref="ActiveNotificationMonitor"/>）の暫定定数
/// （architecture.md §9 実測待ち一覧 M-16。Issue #149）。
/// </summary>
/// <remarks>
/// <see cref="Yagura.Ingestion.PipelineConstants"/>・<see cref="Yagura.Storage.Spool.SpoolConstants"/>
/// と同じ運用——ここに定義する値はすべて「実測で確定するまでの暫定値」であり、実利用・実測を
/// 経て確定したら全体設計書へ転記し、このクラスの値も合わせて更新する。
/// </remarks>
public static class ActiveNotificationConstants
{
    /// <summary>
    /// 周期監視の間隔（暫定値: 1 分）。夜間にスプールが満ちていく最悪シナリオ（architecture.md
    /// §4.6）を数分〜数十分の遅延で検知できることを優先し、書き込み・SQL 問い合わせの頻度は
    /// 低く抑える値として選んだ（実測未実施）。
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// スプール退避が「継続している」と判定するまでの最短継続時間（暫定値: 5 分）。
    /// <see cref="Yagura.Host.Observability.SystemStatusReader.ObservationWindow"/>（UI の状態帯
    /// 判定窓。同じく仮値 5 分）と揃えた——「UI 上は警告あり」と「イベントログで能動通知」が
    /// ほぼ同時に成立することを狙った実装判断（実測未実施）。
    /// </summary>
    public static readonly TimeSpan EvacuationContinuationDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 各トリガの警告連発を抑制する最小間隔（暫定値: 15 分）。
    /// <see cref="Yagura.Ingestion.Persistence.PersistenceWriter"/> の恒久障害警告の抑制窓
    /// （5 分）より長く取った——本監視は「状態が変わらない限り毎周期検知し続ける」性質のトリガ
    /// （スプール上限接近・退避継続・ディスク空き容量・Express 上限接近）が中心であり、
    /// 5 分間隔だと一晩で多数のイベントログ書き込みが発生し、本来の目的（運用者が翌朝に気づく
    /// こと）に対して過剰になるため、より緩い間隔を選んだ（実測未実施）。
    /// </summary>
    public static readonly TimeSpan SuppressionWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// スプール使用量が「上限到達」と判定する比率（<c>UsageRatio &gt;= 1.0</c>）。
    /// <see cref="Yagura.Host.Observability.SystemStatusReader.SpoolNearLimitRatio"/>（0.8。
    /// 「接近」の判定に流用する）と対で使う。
    /// </summary>
    public const double SpoolReachedRatio = 1.0;

    /// <summary>
    /// 監視対象ボリューム（データルート・スプール置き場所。同一ボリュームなら 1 件に重複排除
    /// ——<see cref="MonitoredVolumeInfo"/>）の空き容量の警告閾値（暫定値: 1 GiB）。
    /// スプールの既定使用量上限（<see cref="Yagura.Storage.Spool.SpoolConstants.DefaultQuotaBytes"/>
    /// も 1 GiB）と同じ値を採った——空き容量がスプールの既定上限を下回ると、スプール自体が
    /// 設定上の上限まで育つ前にディスクが枯渇し得るため、この値を安全側の閾値とした
    /// （実測未実施。ディスク全体に対する比率ではなく絶対値にしたのは、ターゲット環境の
    /// ディスクサイズが多様であり、比率だと大容量ディスクでは閾値が実質無意味に大きくなる
    /// ため——本 Issue の実装判断）。
    /// </summary>
    public const long MonitoredVolumeFreeSpaceMinBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// SQL Server Express の DB 容量が「上限接近」と判定する比率（暫定値: 0.8。
    /// <see cref="Yagura.Host.Observability.SystemStatusReader.SpoolNearLimitRatio"/> と同じ値を
    /// 「接近」の一般的な閾値として流用した）。
    /// </summary>
    public const double ExpressNearLimitRatio = 0.8;

    /// <summary>
    /// スプールの定期自己検証（architecture.md §3.2.5。Issue #152）が合成レコードを投入する周期
    /// （仮値: 1 日 1 回。architecture.md §9 M-11 で既に確定済みの仮値をそのまま採用する）。
    /// </summary>
    public static readonly TimeSpan SelfTestInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// 定期自己検証の合成レコードが、投入から drain に合流判定される（DB 書き込み直前で
    /// 破棄される）までの期待時間（暫定値: 10 分。M-16）。drain は通常
    /// <see cref="Yagura.Storage.Spool.SpoolConstants.DrainPollInterval"/>（仮値 200ms）周期で
    /// アクティブセグメントを封止・列挙するため、健全な経路では数秒以内に合流判定される想定——
    /// 本値は「一時的な Q2 高水位による drain 停止（§3.2.2 のヒステリシス）」程度の遅延は
    /// 誤検知しない安全側の余裕を持たせつつ、経路の恒久的な破損（ACL 変化・パス不整合等）は
    /// 1 周期（1 分。<see cref="PollInterval"/>）×数回のうちに検知できる値として選んだ
    /// （実測未実施）。
    /// </summary>
    public static readonly TimeSpan SelfTestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// サーバ証明書の有効期限接近を警告し始める残余期間（暫定値: 30 日）。管理リスナのリモート
    /// HTTPS（ADR-0010 Phase 2 決定 4。EventId 1014）と TLS 受信（security.md §6。EventId 1017）の
    /// 両用途で共有する（#359 で命名を中立化した——同じ目的の閾値であり、用途別の値を採用すべき
    /// 設計上の根拠が無い）。商用 CA・社内 CA の一般的な更新リードタイム（申請〜発行〜
    /// 差し替えに数日〜数週間）を吸収でき、かつ「まだ 1 か月ある」段階からの警告が抑制窓
    /// （<see cref="SuppressionWindow"/>）により 15 分に 1 回に留まることを踏まえた選定
    /// （実測・実運用フィードバック未実施——他の M-16 系仮値と同じ扱い）。
    /// </summary>
    public static readonly TimeSpan CertificateExpiryWarningWindow = TimeSpan.FromDays(30);
}
