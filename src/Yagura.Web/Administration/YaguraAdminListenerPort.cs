namespace Yagura.Web.Administration;

/// <summary>
/// 管理リスナの実ポート番号一式の DI 供給用ラッパー（M8-4。Issue #71。ADR-0010 Phase 2 決定 1 で
/// 複数ポート対応に拡張——リモートバインド有効時は loopback 用ポートに加え、別ポートの
/// リモート HTTPS エントリが加わる。<see cref="ListenerBindPlan"/>/<c>Program</c> 参照）。
/// </summary>
/// <param name="Ports">
/// 管理リスナが実際に bind している全ポート（<c>Yagura.Host</c> の解決済み具体値——OS 採番
/// （0 指定）時も解決済み。<c>ListenerPortGuardMiddleware</c> へ渡す集合と同一でなければならない）。
/// 先頭要素は既定で常に開いている loopback 用ポート（<c>Admin:HttpPort</c>）とする。
/// </param>
/// <remarks>
/// circuit のリスナ帰属判定（<c>YaguraCircuitHandler</c>）・管理画面の circuit 層ガード
/// （<c>AdminScreenAccessPolicy</c>）・上限ガード（<c>CircuitGuardMiddleware</c>）が参照する。
/// 生の <see cref="int"/> の集合をシングルトン登録すると意味が失われるため専用型で包む。
/// </remarks>
public sealed record YaguraAdminListenerPort(IReadOnlyList<int> Ports)
{
    /// <summary>
    /// 単一ポートのみを持つ構成向けの簡易コンストラクタ（既存呼び出し元・テストの互換用）。
    /// </summary>
    public YaguraAdminListenerPort(int port) : this([port])
    {
    }

    /// <summary>
    /// 既定（loopback）の管理ポート。表示用 URL の組み立て等、単一ポートの参照が必要な場面
    /// （<c>AdminScreenLayout.razor</c> のログイン誘導リンク等）で使う——常に <see cref="Ports"/>
    /// の先頭要素（loopback 用ポート）を指す。
    /// </summary>
    public int Port => Ports[0];

    /// <summary>指定したポートが管理リスナの bind 先のいずれかと一致するかどうか。</summary>
    public bool Contains(int port) => Ports.Contains(port);
}
