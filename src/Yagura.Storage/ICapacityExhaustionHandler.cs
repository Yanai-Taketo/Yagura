namespace Yagura.Storage;

/// <summary>
/// 容量枯渇（<see cref="LogStoreFailureKind.CapacityExhausted"/>）を契機に、保持期間削除の
/// 前倒し実行を試みる自走復旧の挿入点（database.md §1.2 契約 3・§3・§4・§5.3）。
/// </summary>
/// <remarks>
/// <para>
/// <b>設計判断（本 Issue の独自判断）</b>: 永続化段（<c>PersistenceWriter</c>）・drain
/// コーディネータ（<c>SpoolDrainCoordinator</c>）は「容量枯渇を検知する場所」ではあるが、
/// 「保持期間（日数）の設定値を知り、削除の実行判断（§3 の譲歩条件の例外扱いを含む）を行う」
/// 責務は本来ホスト層（設定・スケジューラ）にある。両者を疎結合にするため、本インターフェースを
/// 永続化モジュール（Yagura.Ingestion.Persistence）とホスト（Yagura.Host の保持期間スケジューラ）
/// の境界として導入した。<c>PersistenceWriter</c>・<c>SpoolDrainCoordinator</c> は
/// <see cref="LogStoreFailureKind.CapacityExhausted"/> を検知した時点で本インターフェースを
/// 呼び出すのみで、削除の実行そのものには関与しない。
/// </para>
/// <para>
/// <b>実行経路</b>: 呼び出しは fire-and-forget 的な「試みる」であり、失敗（保持期間が未設定
/// = 「削除しない」既定・削除自体の失敗等）を永続化段の書き込みループへ波及させない
/// （実装側で例外を握りつぶす想定。呼び出し元は結果を待たない）。
/// </para>
/// </remarks>
public interface ICapacityExhaustionHandler
{
    /// <summary>
    /// 容量枯渇を通知し、保持期間削除の前倒し実行を試みさせる。
    /// </summary>
    void OnCapacityExhausted();
}
