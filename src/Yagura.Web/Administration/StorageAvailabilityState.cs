namespace Yagura.Web.Administration;

/// <summary>
/// 管理者アカウントストア（アプリ独自認証の台帳）が利用可能かどうかの稼働中の状態
/// （ADR-0023 決定 1）。DI シングルトンとして共有し、認証・画面表示・能動通知が同じ値を見る。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ可変の状態オブジェクトなのか</b>: <c>ViewerHttpsRuntimeState</c>（ADR-0022）のような
/// 起動時固定の <c>record</c> ではなく、稼働中に縮退 → 回復と変化するため。保存先が復旧すると
/// 周期監視の再初期化が成功して <see cref="MarkAvailable"/> が呼ばれ、運用者の操作なしに戻る
/// （スプールの drain と同じ体験に揃える——ADR-0023 決定 1）。
/// </para>
/// <para>
/// <b>スレッド安全性</b>: 書き手は周期監視（単一スレッド）、読み手は circuit・HTTP の複数スレッド。
/// 参照型フィールドの単純な差し替えのみで構成し、読み取り側は 1 回のスナップショット取得
/// （<see cref="Current"/>）で一貫した値を得る。
/// </para>
/// </remarks>
public sealed class StorageAvailabilityState
{
    private volatile Snapshot _current = Snapshot.Healthy;

    /// <summary>現在の状態のスナップショット（読み取りは常にここを 1 回だけ参照する）。</summary>
    public Snapshot Current => _current;

    /// <summary>
    /// アプリ独自認証が利用可能か。<see langword="false"/> の間、ログイン要求は
    /// 「一時的に利用できない」として拒否される（ADR-0023 決定 1。資格情報の誤りとは区別する）。
    /// </summary>
    public bool IsAdminAccountStoreAvailable => _current.Available;

    /// <summary>縮退へ遷移する（初期化失敗時。周期監視から呼ばれる）。</summary>
    /// <param name="failureKind">
    /// 失敗の種別（例外型名）。**運用者向けの分類にのみ使い、ログイン応答には出さない**
    /// （ADR-0023 決定 1——応答に保存先名・例外文字列・再試行時刻を含めない）。
    /// </param>
    public void MarkUnavailable(string? failureKind)
    {
        var previous = _current;
        if (!previous.Available)
        {
            // 既に縮退中: 開始時刻は最初の観測を保つ（「縮退していた窓」を事後に確定するため）。
            return;
        }

        _current = new Snapshot(Available: false, FailureKind: failureKind);
    }

    /// <summary>利用可能へ復帰する（初期化成功時）。</summary>
    public void MarkAvailable() => _current = Snapshot.Healthy;

    /// <summary>状態のスナップショット。</summary>
    /// <param name="Available">アプリ独自認証の台帳が利用可能か。</param>
    /// <param name="FailureKind">縮退中の失敗種別（利用可能なら <see langword="null"/>）。</param>
    public sealed record Snapshot(bool Available, string? FailureKind)
    {
        /// <summary>利用可能（既定）。</summary>
        public static readonly Snapshot Healthy = new(true, null);
    }
}
