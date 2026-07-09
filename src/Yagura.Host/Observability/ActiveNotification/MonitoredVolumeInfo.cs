namespace Yagura.Host.Observability.ActiveNotification;

/// <summary>
/// 監視対象ボリューム 1 つ分の容量読み取り（architecture.md §4.6「データルートのボリュームを
/// DriveInfo で監視」。Issue #149・PR #188 レビュー指摘によりスプール置き場所のボリュームも対象に含める）。
/// </summary>
/// <param name="VolumeRoot">ボリュームのルート（例: <c>C:\</c>。警告メッセージ・抑制窓のキーに使う）。</param>
/// <param name="TotalSizeBytes">ボリュームの総サイズ（バイト）。</param>
/// <param name="AvailableFreeSpaceBytes">現在の空き容量（バイト。ユーザークォータ考慮後——<see cref="System.IO.DriveInfo.AvailableFreeSpace"/> と同義）。</param>
public sealed record MonitoredVolumeReading(string VolumeRoot, long TotalSizeBytes, long AvailableFreeSpaceBytes);

/// <summary>
/// 監視対象ボリューム群の読み取り口（テスト用の差し替え口。実装は
/// <see cref="MonitoredVolumeInfo"/>）。
/// </summary>
public interface IMonitoredVolumeInfo
{
    /// <summary>
    /// 現在の読み取りを返す（同一ボリュームに載る複数パスは 1 件に重複排除済み）。
    /// 取得できないパス（ドライブが準備できていない・UNC ルート・パス解決に失敗した等）は
    /// 結果から除外する——安全側（警告を出さない）に倒す判断（本 Issue の実装判断。カウンタ・
    /// ゲージは本監視と独立したチャネルとして残るため、本監視の取得不能自体が沈黙の唯一の経路には
    /// ならない）。
    /// </summary>
    IReadOnlyList<MonitoredVolumeReading> ReadMonitoredVolumes();
}

/// <summary>
/// <see cref="IMonitoredVolumeInfo"/> の既定実装。<see cref="System.IO.DriveInfo"/> で
/// 各監視対象パス（データルート・スプール置き場所）のドライブ（ボリューム）情報を読む。
/// </summary>
/// <remarks>
/// <para>
/// <b>複数パスを受け取り、ボリューム単位に重複排除する</b>（PR #188 レビュー指摘）:
/// `Spool:Directory` は設定で独立に変更でき（configuration.md §8「スプール」区分）、データルートと
/// 別ドライブに向き得る。その場合、スプールが実際に育っていく先のボリューム（architecture.md §4.6 が
/// 名指しする「夜間にスプールが満ちていく最悪シナリオ」の現場）を監視対象から外さないよう、
/// スプール置き場所のボリュームも読む。既定構成（スプールはデータルート配下）では両者は同一
/// ボリュームであり、重複排除により読み取り・警告とも 1 件に畳まれる。
/// </para>
/// <para>
/// <b>既知の制限（v0.1）</b>: (1) UNC パス（<c>\\server\share\...</c>）は <see cref="DriveInfo"/> が
/// 受け付けないため監視対象から外れる（クラッシュせず「取得不能」へ縮退する）。
/// (2) NTFS ジャンクション・マウントポイントでリダイレクトされたパスは、<see cref="Path.GetFullPath(string)"/> /
/// <see cref="Path.GetPathRoot(string)"/> が文字列操作のみでリパースポイントの実体解決を行わないため、
/// リンク元のドライブの空き容量を報告する（実データが置かれるドライブと異なり得る）。
/// いずれも警告の空振り・鳴き損ないにとどまりデータ破壊は起こさないため、v0.1 では制限として
/// 明記するにとどめる（architecture.md §9 M-16 にも記録）。
/// </para>
/// </remarks>
public sealed class MonitoredVolumeInfo : IMonitoredVolumeInfo
{
    private readonly IReadOnlyList<string> _paths;

    /// <param name="paths">監視対象のパス群（データルート・スプール置き場所等。同一ボリュームは読み取り時に重複排除する）。</param>
    public MonitoredVolumeInfo(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0)
        {
            throw new ArgumentException("監視対象のパスが 1 つも指定されていない。", nameof(paths));
        }

        _paths = paths;
    }

    /// <inheritdoc />
    public IReadOnlyList<MonitoredVolumeReading> ReadMonitoredVolumes()
    {
        var readings = new List<MonitoredVolumeReading>(_paths.Count);
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in _paths)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrEmpty(root) || !seenRoots.Add(root))
                {
                    continue;
                }

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    continue;
                }

                readings.Add(new MonitoredVolumeReading(root, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                // 取得不能はインターフェースの remarks どおり安全側（警告なし）に倒す。
                continue;
            }
        }

        return readings;
    }
}
