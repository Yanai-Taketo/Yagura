namespace Yagura.Web.Circuits;

/// <summary>
/// circuit 1 本分のスコープ付きコンテキスト（M8-4。Issue #71）。
/// </summary>
/// <remarks>
/// <para>
/// Blazor Interactive Server の circuit ごとの DI スコープに 1 インスタンス存在する
/// （<c>AddScoped</c> 登録。circuit スコープで <see cref="YaguraCircuitHandler"/> と
/// コンポーネント群が同一インスタンスを共有する）。役割は 2 つ:
/// </para>
/// <list type="number">
/// <item><b>リスナ帰属の保持</b>: circuit 確立時の接続が管理リスナ（loopback 専用ポート）
/// だったかを保持する。管理画面（<c>Yagura.Web.Administration.Screens</c>）は circuit 上の
/// 対話的な描画・操作の前にこの値を検査する——ルーティング層のポートガード
/// （<c>ListenerPortGuardMiddleware</c>）は「circuit を確立するまでの経路」しか守れず、
/// 確立済み circuit 上の対話的ナビゲーションはルーティングに現れない（security.md §1 L-5 の
/// 覆域の限界）ため、circuit 層の帰属検査がこの限界を埋める。</item>
/// <item><b>協調切断の伝達</b>: 管理者による個別切断（security.md §2.2）・無操作回収（SEC-8）の
/// 要求を circuit 内のコンポーネント（<c>CircuitGovernor</c>）へ伝える。ASP.NET Core は
/// circuit をサーバ側から直接終了させる公開 API を持たない（<c>Circuit</c> 型の公開メンバーは
/// <c>Id</c> のみ——.NET 10.0.9 の <c>Microsoft.AspNetCore.Components.Server</c> を実機で
/// リフレクション確認済み。2026-07-06）ため、circuit 内のコンポーネントが強制の全ページ
/// 再読み込み（circuit を要しない案内ページへの遷移）を行う協調方式を採る。</item>
/// </list>
/// </remarks>
public sealed class YaguraCircuitContext
{
    private Func<string, Task>? _terminationRequested;

    /// <summary>
    /// この circuit が管理リスナ（loopback 専用ポート）経由で確立されたか。
    /// <see langword="null"/> は帰属を判定できなかった状態であり、管理側としては扱わない
    /// （安全側 = 閲覧相当）。値は <see cref="YaguraCircuitHandler"/> が circuit 確立時に設定する。
    /// </summary>
    public bool? IsAdminListener { get; internal set; }

    /// <summary>circuit 確立時の接続元アドレス（監査記録・一覧表示用）。</summary>
    public string? RemoteAddress { get; internal set; }

    /// <summary>
    /// 切断要求の購読（<c>CircuitGovernor</c> が circuit の初期化時に登録する）。引数は切断理由
    /// （利用者向け文言ではなく内部識別子。案内ページの表示分岐に使う）。
    /// </summary>
    public event Func<string, Task> TerminationRequested
    {
        add => _terminationRequested += value;
        remove => _terminationRequested -= value;
    }

    /// <summary>
    /// 切断を要求する（管理者の個別切断・無操作回収）。購読者（circuit 内のコンポーネント）が
    /// いない場合は何もせず <see langword="false"/> を返す——協調方式の限界であり、呼び出し側は
    /// 受理可否を結果で観測できる。
    /// </summary>
    public async Task<bool> RequestTerminationAsync(string reason)
    {
        var handler = _terminationRequested;
        if (handler is null)
        {
            return false;
        }

        await handler(reason).ConfigureAwait(false);
        return true;
    }
}
