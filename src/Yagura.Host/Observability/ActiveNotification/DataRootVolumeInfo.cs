namespace Yagura.Host.Observability.ActiveNotification;

/// <summary>
/// データルートを置くボリュームの容量読み取り（architecture.md §4.6「データルートのボリューム
/// を DriveInfo で監視」。Issue #149 のオーナー承認済み提案）。
/// </summary>
/// <param name="TotalSizeBytes">ボリュームの総サイズ（バイト）。</param>
/// <param name="AvailableFreeSpaceBytes">現在の空き容量（バイト。ユーザークォータ考慮後——<see cref="System.IO.DriveInfo.AvailableFreeSpace"/> と同義）。</param>
public sealed record DataRootVolumeReading(long TotalSizeBytes, long AvailableFreeSpaceBytes);

/// <summary>
/// <see cref="DataRootVolumeReading"/> の読み取り口（テスト用の差し替え口。実装は
/// <see cref="DataRootVolumeInfo"/>）。
/// </summary>
public interface IDataRootVolumeInfo
{
    /// <summary>
    /// 現在の読み取りを返す。取得できない場合（ドライブが準備できていない・パス解決に失敗した等）は
    /// <c>null</c> を返す——安全側（警告を出さない）に倒す判断（本 Issue の実装判断。カウンタ・
    /// ゲージは本監視と独立したチャネルとして残るため、本監視の取得不能自体が沈黙の唯一の経路には
    /// ならない）。
    /// </summary>
    DataRootVolumeReading? TryRead();
}

/// <summary>
/// <see cref="IDataRootVolumeInfo"/> の既定実装。<see cref="System.IO.DriveInfo"/> でデータルートの
/// ドライブ（ボリューム）情報を読む。
/// </summary>
public sealed class DataRootVolumeInfo : IDataRootVolumeInfo
{
    private readonly string _dataRoot;

    public DataRootVolumeInfo(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = dataRoot;
    }

    /// <inheritdoc />
    public DataRootVolumeReading? TryRead()
    {
        try
        {
            var fullPath = Path.GetFullPath(_dataRoot);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return null;
            }

            return new DataRootVolumeReading(drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // 取得不能はクラスの remarks どおり安全側（警告なし）に倒す。
            return null;
        }
    }
}
