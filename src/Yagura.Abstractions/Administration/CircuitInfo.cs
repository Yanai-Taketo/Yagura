namespace Yagura.Abstractions.Administration;

/// <summary>
/// circuit 1 件分の表示情報（security.md §2.2「circuit 一覧（接続元・確立時刻・最終活動時刻）」）。
/// </summary>
/// <param name="CircuitId">circuit の識別子（Blazor が採番する ID）。</param>
/// <param name="RemoteAddress">接続元アドレス（circuit 確立時の接続から取得。取得不能時は null）。</param>
/// <param name="IsAdminListener">
/// 管理リスナ経由の circuit か（<see langword="null"/> は帰属を判定できなかった circuit——
/// 判定不能は管理側として扱わない = 安全側）。
/// </param>
/// <param name="OpenedAt">確立時刻（UTC）。</param>
/// <param name="LastActivityAt">最終活動時刻（UTC。SEC-8 の「操作」= circuit 上の入力活動）。</param>
public sealed record CircuitInfo(
    string CircuitId,
    string? RemoteAddress,
    bool? IsAdminListener,
    DateTimeOffset OpenedAt,
    DateTimeOffset LastActivityAt);
