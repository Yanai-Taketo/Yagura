namespace Yagura.Web.ForwarderKit;

/// <summary>
/// フォワーダ MSI アップロード（配置経路 (b)。ADR-0020 決定 1）の実効値を Web 層へ渡す
/// ランタイムオプション（<c>AdminAuthenticationRuntimeOptions</c> と同じ受け渡しパターン。
/// Host の <c>Program</c> が検証済みの実効値で登録する）。
/// </summary>
/// <param name="Enabled">
/// アップロード機能の opt-in（<c>Admin:ForwarderKit:MsiUpload:Enabled</c>）の実効値。
/// <see langword="true"/> は起動時 fail-closed（1032）により「管理 UI 認証が有効 +
/// <c>RequireForLoopback</c> 有効（= 管理リスナに無認証の到達経路が存在しない）」を含意する。
/// 生成画面はこの値でアップロード区画の表出/非表出を分岐し、非表出時はどの前提条件が
/// 欠けているかを <c>AdminAuthenticationRuntimeOptions</c> から導出して案内する（決定 1
/// 「沈黙にしない」）。
/// </param>
/// <param name="ServiceAccountName">
/// サービスの実効実行アカウント名（既定 <c>NT SERVICE\Yagura</c> / gMSA 構成では
/// <c>DOMAIN\name$</c>）。画面が ACE 付与コマンド（<c>icacls ... /grant "アカウント:(OI)(CI)(M)"</c>）を
/// コピー可能な形で案内するために使う（付与先は実効実行アカウントから導出する——ADR-0020
/// 決定 2・security.md §5.2 の原則）。
/// </param>
public sealed record ForwarderMsiUploadRuntimeOptions(bool Enabled, string? ServiceAccountName = null);
