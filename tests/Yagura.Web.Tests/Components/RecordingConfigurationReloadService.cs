using Yagura.Abstractions.Administration;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 再読み込みの呼び出し回数を数え、指定した結果を返す <see cref="IConfigurationReloadService"/> のフェイク。
/// 管理画面の保存後に再読み込み（＝再起動待ちカードへの計上）が走るかを固定するテストで使う。
/// </summary>
internal sealed class RecordingConfigurationReloadService : IConfigurationReloadService
{
    internal int ReloadCount { get; private set; }

    /// <summary>次の <see cref="ReloadAsync"/> が返す結果（既定は変更なしの成功）。</summary>
    internal ConfigurationReloadResult NextResult { get; set; } = Succeeded([]);

    public IReadOnlyList<PendingRestartKey> GetPendingRestartKeys() => [];

    public Task<ConfigurationReloadResult> ReloadAsync(
        string? operatorAddress,
        string? authenticationScheme,
        string? authenticatedPrincipal,
        CancellationToken cancellationToken = default)
    {
        ReloadCount++;
        return Task.FromResult(NextResult);
    }

    /// <summary>適用の口が無い変更キーがすべて再起動待ちに載った、成功時の結果。</summary>
    internal static ConfigurationReloadResult Succeeded(IReadOnlyList<string> pendingRestartKeys) => new(
        Rejected: false,
        RejectionReason: null,
        ChangedKeys: pendingRestartKeys,
        AppliedKeys: [],
        PendingRestartKeys: pendingRestartKeys,
        WarningMessages: [],
        UnknownKeys: [],
        TypeCoercionNotes: []);

    /// <summary>設定ファイルの検証失敗で再読み込みが拒否された結果。</summary>
    internal static ConfigurationReloadResult RejectedWith(string reason) => new(
        Rejected: true,
        RejectionReason: reason,
        ChangedKeys: [],
        AppliedKeys: [],
        PendingRestartKeys: [],
        WarningMessages: [],
        UnknownKeys: [],
        TypeCoercionNotes: []);
}
