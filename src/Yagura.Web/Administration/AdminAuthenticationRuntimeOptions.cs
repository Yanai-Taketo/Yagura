namespace Yagura.Web.Administration;

/// <summary>
/// 管理 UI 認証の実効設定値の DI 供給用ラッパー（ADR-0010 Phase 1。
/// <see cref="YaguraAdminListenerPort"/> と同じ「Host が解決した値を専用型で Web へ渡す」パターン）。
/// </summary>
/// <param name="RequireAuthentication">
/// loopback アクセスにも認証を課す opt-in の実効値（<c>Admin:Authentication:RequireForLoopback</c>。
/// ADR-0010 決定 1）。<see langword="true"/> のとき、<c>AdminScreenLayout</c> と
/// <c>YaguraAdminExtensions.MapYaguraAdmin</c> の両方が認可を要求する。
/// </param>
/// <param name="WindowsAuthEnabled">Windows 統合認証（Negotiate）が有効か。ログイン画面の表示分岐に使う。</param>
/// <param name="AppAuthEnabled">アプリ独自 ID/パスワード認証が有効か。ログイン画面の表示分岐に使う。</param>
public sealed record AdminAuthenticationRuntimeOptions(bool RequireAuthentication, bool WindowsAuthEnabled, bool AppAuthEnabled);
