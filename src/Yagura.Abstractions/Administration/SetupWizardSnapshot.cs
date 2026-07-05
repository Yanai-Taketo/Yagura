namespace Yagura.Abstractions.Administration;

/// <summary>
/// 初期セットアップウィザードのセッション状態のスナップショット（画面表示用。
/// configuration.md §7「再開時に『どこから再開しているか』を明示する」の材料）。
/// </summary>
/// <param name="ConfirmedSteps">確定済みステップ（確定順）。</param>
/// <param name="NextStep">次に入力すべきステップ。</param>
/// <param name="ConfirmedValues">確定済みの入力値（キーは <see cref="SetupWizardValueKeys"/>）。</param>
/// <param name="ApplyIdempotencyToken">
/// 適用（<see cref="ISetupWizardService.ApplyAsync"/>）に使う冪等トークン
/// （<see cref="SetupWizardStep.Review"/> 確定後に非 null。configuration.md §7「確定操作は一回性を保証する」）。
/// </param>
/// <param name="Applied">適用済みかどうか。</param>
public sealed record SetupWizardSnapshot(
    IReadOnlyList<SetupWizardStep> ConfirmedSteps,
    SetupWizardStep NextStep,
    IReadOnlyDictionary<string, string> ConfirmedValues,
    string? ApplyIdempotencyToken,
    bool Applied);

/// <summary>
/// <see cref="SetupWizardSnapshot.ConfirmedValues"/> のキー名（設定ファイルのキーパスと同一表記。
/// configuration.md §8 の確定済みキーに対応する）。
/// </summary>
public static class SetupWizardValueKeys
{
    public const string UdpPort = "Ingestion:Udp:Port";
    public const string TcpPort = "Ingestion:Tcp:Port";
    public const string ViewerHttpPort = "Viewer:HttpPort";
    public const string ViewerPublicAccess = "Viewer:PublicAccess";
    public const string AdminHttpPort = "Admin:HttpPort";
    public const string RetentionDays = "Retention:Days";
}
