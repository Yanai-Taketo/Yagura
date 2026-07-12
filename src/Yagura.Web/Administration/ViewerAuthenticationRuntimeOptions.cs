namespace Yagura.Web.Administration;

/// <summary>
/// 閲覧 UI 認証（ADR-0010 Phase 4 決定 7）の実効設定値の DI 供給用ラッパー
/// （<see cref="AdminAuthenticationRuntimeOptions"/> と対の閲覧側。Host が解決した値を専用型で Web へ渡す）。
/// </summary>
/// <param name="Enabled">
/// 閲覧 UI 認証（<c>Viewer:Authentication:Windows:Enabled</c>）が有効か。<see langword="true"/> のとき、
/// 閲覧リスナ経由の閲覧画面・CSV に <see cref="AdminAuthenticationExtensions.ViewerPolicyName"/> の認可を課し、
/// <c>MainLayout</c> の circuit 層 viewer ガードが認証状態を検査する。既定 <see langword="false"/>＝
/// 現状維持（認証なし・LAN 公開——体験は一切変わらない）。
/// </param>
/// <param name="AppAuthAvailable">
/// 閲覧ログイン画面でアプリ独自 ID/パスワードのログインを提示するか（オーナー決定 2026-07-12——閲覧ログインは
/// Windows + アプリの両方を受ける）。アプリ独自アカウントは管理役割のみ（決定 5）だが、管理 ⊇ 閲覧のため
/// 閲覧リスナ（8514）でも閲覧に到達できる。実効値はアプリ独自認証の有効/無効（<c>Admin:Authentication:App:Enabled</c>）
/// ——アプリアカウントストアは管理・閲覧で共有の単一ストアのため。
/// </param>
public sealed record ViewerAuthenticationRuntimeOptions(bool Enabled, bool AppAuthAvailable)
{
    /// <summary>閲覧認証無効（既定）。</summary>
    public static readonly ViewerAuthenticationRuntimeOptions Disabled = new(Enabled: false, AppAuthAvailable: false);
}
