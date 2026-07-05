namespace Yagura.Abstractions.Administration;

/// <summary>
/// circuit 管理（一覧・個別切断）の契約（security.md §2.2「circuit の可視化と選択的切断」。
/// M8-4。Issue #71）。書き込み系サービスであるため <see cref="IYaguraWriteService"/> を実装する
/// （切断はサーバ状態の変更 = 管理操作。閲覧リスナのシステム状態画面には一覧を置かない——
/// security.md §2.2 の線引き）。
/// </summary>
public interface ICircuitManagementService
    : IYaguraWriteService
{
    /// <summary>現在の circuit 一覧（接続元・確立時刻・最終活動時刻。security.md §2.2）。</summary>
    IReadOnlyList<CircuitInfo> ListCircuits();

    /// <summary>
    /// circuit を個別に切断する（管理操作として監査記録（2000 番台）の対象）。
    /// </summary>
    /// <returns>
    /// 切断要求を受け付けた場合 <see langword="true"/>。対象が既に存在しない場合
    /// <see langword="false"/>（この場合は監査記録しない——実行されなかった操作）。
    /// </returns>
    Task<bool> DisconnectAsync(
        string circuitId,
        string? operatorAddress = null,
        CancellationToken cancellationToken = default);
}
