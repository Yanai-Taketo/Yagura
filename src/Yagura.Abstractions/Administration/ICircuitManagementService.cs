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
    /// <param name="circuitId">対象 circuit の識別子。</param>
    /// <param name="operatorAddress">操作者の接続元アドレス（監査記録用）。</param>
    /// <param name="operatorScheme">操作者の認証方式（ADR-0010 決定 6。未認証では <see langword="null"/>）。</param>
    /// <param name="operatorPrincipal">操作者の認証済み利用者名（同上）。</param>
    Task<bool> DisconnectAsync(
        string circuitId,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default);
}
