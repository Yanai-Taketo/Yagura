namespace Yagura.Web.Administration;

/// <summary>
/// 閲覧 UI HTTPS（ADR-0022）の起動時判定の実効状態（決定 2 可視化③——管理画面の常設バナーの
/// データ源。Issue #455 段階 ③）。<c>Program</c> が起動時の証明書解決の結果から構築して DI 登録する
/// （<c>ViewerAuthenticationRuntimeOptions</c> と同じ「起動時に確定する実効値を Web 層へ渡す」型）。
/// </summary>
/// <param name="ListenerSuppressed">
/// 閲覧リスナが縮小継続で開かれていない（<c>Viewer:Https:*</c> が構成されているのに証明書が
/// 使えない——起動時警告 1035 の状態）なら <see langword="true"/>。メール通知未設定の環境では
/// 「RDP して管理画面を開いた瞬間に分かる」ことが現実的な発見経路になるため、管理画面に常設
/// バナーを表示する（ADR-0022 決定 2。ペルソナレビュー——佐藤——の指摘）。
/// </param>
/// <param name="Reason">縮小継続の理由（1035 と同文。<see cref="ListenerSuppressed"/> が偽なら <see langword="null"/>）。</param>
public sealed record ViewerHttpsRuntimeState(bool ListenerSuppressed, string? Reason)
{
    /// <summary>縮小継続なし（既定）。</summary>
    public static readonly ViewerHttpsRuntimeState Normal = new(false, null);
}
