using System.Collections.Concurrent;

namespace Yagura.Web.Circuits;

/// <summary>
/// プロセス内の全 circuit の台帳（security.md §2.2 の可視化・上限・無操作回収の共通基盤。
/// M8-4。Issue #71）。
/// </summary>
/// <remarks>
/// <para>
/// シングルトンとして登録し、circuit ごとの <see cref="YaguraCircuitHandler"/>（スコープ付き）が
/// 確立・活動・終了を報告する。一覧の読み取りは管理画面の circuit 管理
/// （<c>Yagura.Web.Administration.CircuitManagementService</c>）が、上限判定は
/// <c>CircuitGuardMiddleware</c> が、無操作回収は <c>CircuitIdleReclaimService</c> が使う。
/// </para>
/// <para>
/// <b>上限はリスナごとに数える</b>（security.md §2.2「プロセス全体・両リスナ合算の単一上限に
/// しない」）。帰属を判定できなかった circuit（<see cref="CircuitRecord.IsAdminListener"/> が
/// <see langword="null"/>）は閲覧側として数える（管理枠を不明な circuit に食わせない安全側）。
/// </para>
/// </remarks>
public sealed class CircuitRegistry
{
    private readonly ConcurrentDictionary<string, CircuitRecord> _circuits = new(StringComparer.Ordinal);

    /// <summary>現在の circuit 一覧のスナップショット。</summary>
    public IReadOnlyList<CircuitRecord> Snapshot() => _circuits.Values.ToList();

    /// <summary>リスナ別の現在 circuit 数（帰属不明は閲覧側に数える）。</summary>
    public int Count(bool adminListener) =>
        _circuits.Values.Count(c => (c.IsAdminListener == true) == adminListener);

    /// <summary>circuit の確立を登録する。</summary>
    public void Register(CircuitRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _circuits[record.CircuitId] = record;
    }

    /// <summary>circuit の終了を登録から外す。</summary>
    public void Unregister(string circuitId) => _circuits.TryRemove(circuitId, out _);

    /// <summary>circuit 上の入力活動（SEC-8 の「操作」）を記録する。</summary>
    public void RecordActivity(string circuitId, DateTimeOffset now)
    {
        if (_circuits.TryGetValue(circuitId, out var record))
        {
            record.LastActivityAt = now;
        }
    }

    /// <summary>
    /// circuit の個別切断を要求する（security.md §2.2。実際の切断は circuit 側の協調動作——
    /// <see cref="YaguraCircuitContext.RequestTerminationAsync"/> の remarks 参照）。
    /// </summary>
    /// <returns>対象が存在し、切断要求が circuit 内の購読者に受理された場合 <see langword="true"/>。</returns>
    public async Task<bool> RequestDisconnectAsync(string circuitId, string reason)
    {
        if (!_circuits.TryGetValue(circuitId, out var record))
        {
            return false;
        }

        return await record.Context.RequestTerminationAsync(reason).ConfigureAwait(false);
    }

    /// <summary>
    /// 無操作 circuit を回収する（SEC-8。security.md §2.2「一定時間操作のない circuit を切断して
    /// 枠を解放する」。閲覧は長め・管理は短めのタイムアウト——<see cref="CircuitGovernanceDefaults"/>）。
    /// </summary>
    /// <returns>回収要求を発行した circuit の一覧（観測用）。</returns>
    public async Task<IReadOnlyList<CircuitRecord>> ReclaimIdleAsync(DateTimeOffset now)
    {
        var reclaimed = new List<CircuitRecord>();

        foreach (var record in _circuits.Values)
        {
            var timeout = record.IsAdminListener == true
                ? CircuitGovernanceDefaults.AdminIdleTimeout
                : CircuitGovernanceDefaults.ViewerIdleTimeout;

            if (now - record.LastActivityAt >= timeout &&
                await record.Context.RequestTerminationAsync(CircuitTerminationReasons.IdleReclaimed).ConfigureAwait(false))
            {
                reclaimed.Add(record);
            }
        }

        return reclaimed;
    }
}

/// <summary>circuit 台帳の 1 エントリ（security.md §2.2 の一覧項目: 接続元・確立時刻・最終活動時刻）。</summary>
public sealed class CircuitRecord
{
    public CircuitRecord(
        string circuitId,
        string? remoteAddress,
        bool? isAdminListener,
        DateTimeOffset openedAt,
        YaguraCircuitContext context)
    {
        ArgumentException.ThrowIfNullOrEmpty(circuitId);
        ArgumentNullException.ThrowIfNull(context);

        CircuitId = circuitId;
        RemoteAddress = remoteAddress;
        IsAdminListener = isAdminListener;
        OpenedAt = openedAt;
        LastActivityAt = openedAt;
        Context = context;
    }

    public string CircuitId { get; }

    public string? RemoteAddress { get; }

    /// <summary><see langword="null"/> = 帰属を判定できなかった circuit（閲覧側として扱う）。</summary>
    public bool? IsAdminListener { get; }

    public DateTimeOffset OpenedAt { get; }

    /// <summary>最終活動時刻（SEC-8 の「操作」= inbound activity。表示更新の受信は含まない）。</summary>
    public DateTimeOffset LastActivityAt { get; internal set; }

    /// <summary>切断要求の伝達先（circuit スコープの <see cref="YaguraCircuitContext"/>）。</summary>
    public YaguraCircuitContext Context { get; }
}

/// <summary>切断理由の内部識別子（案内ページの表示分岐用。利用者向け文言は UiText）。</summary>
public static class CircuitTerminationReasons
{
    /// <summary>管理者による個別切断（security.md §2.2）。</summary>
    public const string DisconnectedByAdministrator = "disconnected";

    /// <summary>無操作回収（SEC-8）。</summary>
    public const string IdleReclaimed = "idle";
}
