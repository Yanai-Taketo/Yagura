namespace Yagura.Web.Administration;

/// <summary>
/// 管理リスナの実ポート番号の DI 供給用ラッパー（M8-4。Issue #71）。
/// </summary>
/// <param name="Port">
/// 管理リスナの実ポート（<c>Yagura.Host</c> の <c>effectiveAdminPort</c>——OS 採番（0 指定）
/// 時も解決済みの具体値。<c>ListenerPortGuardMiddleware</c> へ渡す値と同一でなければならない）。
/// </param>
/// <remarks>
/// circuit のリスナ帰属判定（<c>YaguraCircuitHandler</c>）・管理画面の circuit 層ガード
/// （<c>AdminScreenAccessPolicy</c>）・上限ガード（<c>CircuitGuardMiddleware</c>）が参照する。
/// 生の <see cref="int"/> をシングルトン登録すると意味が失われるため専用型で包む。
/// </remarks>
public sealed record YaguraAdminListenerPort(int Port);
