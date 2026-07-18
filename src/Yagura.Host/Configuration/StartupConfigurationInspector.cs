using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yagura.Abstractions.Auditing;

namespace Yagura.Host.Configuration;

/// <summary>
/// 起動時の設定差分照合（Issue #329）: 前回稼働時に適用されていた設定のスナップショット
/// （<see cref="LastAppliedConfigurationSnapshotStore"/>）と起動時点の設定ファイルを比較し、
/// 差分があれば「前回稼働時から設定ファイルが変更された状態で起動した」を監査 2019 として
/// 1 件記録する。手編集 + サービス再起動で反映された変更がどの監査証跡にも残らない特性
/// （Issue #306。security.md §4.1）への軽量補完。
/// </summary>
/// <remarks>
/// <para>
/// <b>記録は変更キー名のみ</b>: 2016（設定再読み込み）と同粒度で前後値を含めない——秘密情報
/// キーの値の混入を構造的に避ける。比較対象は <see cref="ConfigurationChangePlanner"/> の
/// 登録キーのみ（未知キーは既存の未知キー警告が担う）。
/// </para>
/// <para>
/// <b>限界（security.md §4.1）</b>: 本記録は「何が変わったか」の補完であり「いつ・誰が」は
/// 特定できない。スナップショット自体もサービスアカウント書き込み可のファイルであり、設定を
/// 手編集できる者は消せる——悪意への統制ではなく事故調査のための運用証跡（一次の耐タンパ線は
/// イベントログ併記 = security.md §4.2）。初回起動・スナップショット欠損/破損時は照合を
/// スキップし、その旨のみログに残す。
/// </para>
/// <para>
/// <b>失敗は起動を妨げない</b>: FirewallStartupInspector と同型（Build 後の手動呼び出し・
/// 例外は内部で完結）。監査レール（IAuditRecorder）は例外を投げない契約。
/// </para>
/// </remarks>
public sealed class StartupConfigurationInspector
{
    private readonly string _dataRoot;
    private readonly IAuditRecorder _auditRecorder;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StartupConfigurationInspector> _logger;

    public StartupConfigurationInspector(
        string dataRoot,
        IAuditRecorder auditRecorder,
        TimeProvider? timeProvider = null,
        ILogger<StartupConfigurationInspector>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(auditRecorder);

        _dataRoot = dataRoot;
        _auditRecorder = auditRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<StartupConfigurationInspector>.Instance;
    }

    /// <summary>
    /// 前回適用スナップショットと起動時点の生 options を照合し（差分があれば監査 2019）、
    /// 照合の結果によらず今回の生 options を新しい基準として保存する（保存契機①「起動完了時」——
    /// 検出済みの差分を次回起動で重複報告しない）。
    /// </summary>
    /// <param name="startupOptions">
    /// 起動時に読み込まれた設定ファイルの生 options（Program が捕捉する startupRawOptions。
    /// ConfigurationReloadService の差分計算の初期基準と同一のスナップショット）。
    /// </param>
    public async Task InspectAndRefreshSnapshotAsync(
        YaguraConfigurationOptions startupOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startupOptions);

        try
        {
            var previous = LastAppliedConfigurationSnapshotStore.TryRead(_dataRoot, _logger);
            if (previous is null)
            {
                _logger.LogInformation(
                    "前回適用設定スナップショット（{FileName}）が存在しないため、起動時の設定差分照合をスキップしました" +
                    "（初回起動、またはスナップショットの欠損・破損）。今回の設定を基準として保存します。",
                    LastAppliedConfigurationSnapshotStore.FileName);
            }
            else
            {
                var plan = ConfigurationChangePlanner.Compare(previous, startupOptions);
                if (plan.HasChanges)
                {
                    // 起動時自動照合のため RemoteAddress/AuthenticationScheme は null（2015・2017 と同型）。
                    await _auditRecorder.RecordAsync(
                        new AuditEvent(
                            OccurredAt: _timeProvider.GetUtcNow(),
                            Kind: AuditEventKind.StartupConfigurationChangeDetected,
                            RemoteAddress: null,
                            RemotePort: null,
                            Detail: "起動時照合: 前回稼働時から設定ファイルが変更された状態で起動しました。" +
                                $"変更キー={string.Join(", ", plan.ChangedKeys)}" +
                                "（いつ・誰が変更したかは本記録では特定できない——手編集 + 再起動の経路。security.md §4.1）"),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            LastAppliedConfigurationSnapshotStore.TrySave(_dataRoot, startupOptions, _logger);
        }
        catch (Exception ex)
        {
            // 起動時の fire-and-forget 呼び出しで未観測例外を作らない最終ガード
            // （通常の失敗経路は Store / IAuditRecorder の内部で完結している）。
            _logger.LogWarning(ex, "起動時の設定差分照合に失敗しました（起動は継続します）。");
        }
    }
}
