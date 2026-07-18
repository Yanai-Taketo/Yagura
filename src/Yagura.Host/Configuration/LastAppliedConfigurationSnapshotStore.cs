using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yagura.Host.Configuration;

/// <summary>
/// 「最後に適用した生 options」のスナップショット永続化（Issue #329）。起動時の設定差分照合
/// （<see cref="StartupConfigurationInspector"/>）の比較基準となる専用ファイルを読み書きする。
/// 保存契機は ①起動完了時（照合直後の基準取り直し）②ウィザード保存（監査 2001）
/// ③再読み込み反映（監査 2016）の 3 点。
/// </summary>
/// <remarks>
/// <para>
/// <b>専用ファイルとする理由</b>: メタデータ領域（observability-state.json）は
/// ObservabilityCoordinator が全体書き換えで所有する周期書き込みドメインであり、契機の異なる
/// 書き込みを混載すると書き込み主体が競合する（FirewallStartupInspector のマーカーファイルと
/// 同じ判断）。
/// </para>
/// <para>
/// <b>失敗は呼び出し元の操作を妨げない</b>: 起動・ウィザード保存・再読み込みのいずれの契機でも、
/// スナップショットの読み書き失敗が本体の操作を失敗させてはならない（監査レールと同じ向き——
/// ADR-0004 決定 7）ため、Try 系のみを公開し IO 失敗は警告ログに留める。本スナップショットは
/// 悪意への統制ではなく事故調査のための運用証跡である（security.md §4.1）。
/// </para>
/// <para>
/// <b>原子性</b>: 一時ファイルへ書いてから <see cref="File.Replace(string, string, string?)"/> で
/// 置換する（<see cref="YaguraConfigurationWriter"/> と同じ方式。クラッシュで半分だけ書かれた
/// スナップショットを次回起動の照合基準にしない）。
/// </para>
/// </remarks>
public static class LastAppliedConfigurationSnapshotStore
{
    /// <summary>スナップショットのファイル名（データルート直下）。</summary>
    public const string FileName = "last-applied-configuration.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// スナップショットを読み込む。ファイル不在（初回起動）・読み取り失敗・破損のいずれも
    /// <see langword="null"/> を返す（呼び出し元は照合をスキップし、現在の設定で基準を取り直す）。
    /// </summary>
    public static YaguraConfigurationOptions? TryRead(string dataRoot, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var path = Path.Combine(dataRoot, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<YaguraConfigurationOptions>(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(
                ex,
                "前回適用設定スナップショット（{FileName}）の読み込みに失敗しました。起動時の設定差分照合をスキップし、現在の設定で基準を取り直します。",
                FileName);
            return null;
        }
    }

    /// <summary>
    /// 生 options をスナップショットとして保存する（原子的置換）。失敗は警告ログのみ——
    /// 古い基準が残るため、次回起動の照合は監査済みの変更を重複して報告し得る（見逃しはしない）。
    /// </summary>
    public static void TrySave(string dataRoot, YaguraConfigurationOptions options, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(options);

        var path = Path.Combine(dataRoot, FileName);
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tempPath, JsonSerializer.SerializeToUtf8Bytes(options, SerializerOptions));

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(
                ex,
                "前回適用設定スナップショット（{FileName}）の保存に失敗しました。次回起動の設定差分照合は古い基準と比較されます。",
                FileName);

            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                // 一時ファイルの残骸削除も失敗した場合は放置する（次の保存は別名の一時ファイルを使う）。
            }
        }
    }
}
