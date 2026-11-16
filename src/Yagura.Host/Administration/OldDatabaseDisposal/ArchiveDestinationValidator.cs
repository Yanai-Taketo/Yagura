using Yagura.Abstractions.Administration;

namespace Yagura.Host.Administration.OldDatabaseDisposal;

/// <summary>保管先の検証に通ったときの材料。</summary>
/// <param name="ParentDirectory">保管先（親フォルダ）の入力値を正規化したパス。</param>
/// <param name="Volume">保管先ボリューム。</param>
/// <param name="FloorBytes">保管先ボリュームで書き出し中も守る空き容量の下限。</param>
/// <param name="RequiredBytes">書き出しに要る見込みの容量。</param>
public sealed record ArchiveDestination(
    string ParentDirectory,
    ArchiveVolumeInfo Volume,
    long FloorBytes,
    long RequiredBytes);

/// <summary>保管先の検証の結果。</summary>
public sealed record ArchiveDestinationValidation(
    ArchiveDestination? Destination,
    OldDatabaseDisposalReason? Reason,
    string Message)
{
    /// <summary>通ったか。</summary>
    public bool Accepted => Destination is not null;
}

/// <summary>
/// 保管先の条件（database.md §6.3「退避先の条件」）の検証。何も変更しない——フォルダを作れるかどうかは
/// 作業用サブフォルダを実際に作って確かめる（呼び出し側）。
/// </summary>
public sealed class ArchiveDestinationValidator
{
    private readonly IArchivePlatform _platform;
    private readonly string _dataRoot;
    private readonly OldDatabaseDisposalEnvironment _environment;
    private readonly long _thresholdBytes;

    public ArchiveDestinationValidator(
        IArchivePlatform platform,
        string dataRoot,
        OldDatabaseDisposalEnvironment environment,
        long thresholdBytes)
    {
        _platform = platform;
        _dataRoot = dataRoot;
        _environment = environment;
        _thresholdBytes = thresholdBytes;
    }

    /// <summary>
    /// 書き出しに要る見込みの容量。作業ファイル 1・2 が一時的に同時に存在するため、旧ファイル（本体 +
    /// <c>-wal</c>）の 2 倍とする。
    /// </summary>
    public static long EstimateRequiredBytes(long bodyBytes, long walBytes) => 2 * (bodyBytes + walBytes);

    /// <summary>保管先を検証する。</summary>
    public ArchiveDestinationValidation Validate(string? parentDirectory, long requiredBytes)
    {
        var input = parentDirectory?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            return Reject(OldDatabaseDisposalReason.DestinationNotLocalAbsolutePath, "保管先のフォルダを入力してください。");
        }

        if (_platform.IsNetworkOrDevicePath(input))
        {
            return Reject(
                OldDatabaseDisposalReason.DestinationNetwork,
                "ネットワーク上のフォルダは保管先にできません。このサーバのローカルフォルダに書き出した後、" +
                "管理者としてファイルサーバへ複写してください。");
        }

        if (!_platform.IsLocalAbsolutePath(input))
        {
            return Reject(
                OldDatabaseDisposalReason.DestinationNotLocalAbsolutePath,
                @"保管先は、このサーバのドライブ名から始まるフォルダのパスで入力してください（例: D:\Archive）。");
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Reject(OldDatabaseDisposalReason.DestinationNotLocalAbsolutePath, $"保管先のパスを解釈できません: {ex.Message}");
        }

        if (!Directory.Exists(normalized))
        {
            return Reject(
                OldDatabaseDisposalReason.DestinationMissing,
                "保管先のフォルダが見つかりません。既存のフォルダを指定してください（このフォルダは製品では作りません）。");
        }

        var volume = _platform.GetVolume(normalized);
        if (volume is null)
        {
            return Reject(OldDatabaseDisposalReason.DestinationNoAcl, "保管先のドライブの情報を取得できません。");
        }

        switch (volume.DriveKind)
        {
            case ArchiveDriveKind.Network:
                return Reject(
                    OldDatabaseDisposalReason.DestinationNetwork,
                    "ネットワークドライブは保管先にできません。このサーバのローカルフォルダに書き出した後、" +
                    "管理者としてファイルサーバへ複写してください。");
            case ArchiveDriveKind.Removable:
            case ArchiveDriveKind.Optical:
            case ArchiveDriveKind.Other:
                return Reject(
                    OldDatabaseDisposalReason.DestinationRemovable,
                    "USB メモリ等の取り外せる媒体・光学ドライブは保管先にできません（媒体ごと持ち出されると保護が効きません）。");
        }

        if (!_platform.AclSupported || !volume.PreservesAcl)
        {
            return Reject(
                OldDatabaseDisposalReason.DestinationNoAcl,
                $"保管先のドライブ（{volume.FileSystemName ?? "不明"}）はアクセス権（ACL）を保持できないため使えません。" +
                "NTFS のドライブを指定してください。");
        }

        var resolved = _platform.ResolveFinalPath(normalized);
        foreach (var (managed, label) in ManagedAreas())
        {
            if (ArchivePathRules.IsSameOrUnder(resolved, managed))
            {
                return Reject(
                    OldDatabaseDisposalReason.DestinationInsideManagedArea,
                    $"{label}の中は保管先にできません（製品の管理外へ出すための操作のため）。");
            }
        }

        var floor = FloorFor(volume);
        if (volume.AvailableFreeBytes - requiredBytes < floor)
        {
            return Reject(
                OldDatabaseDisposalReason.InsufficientFreeSpace,
                $"保管先のドライブの空き容量が足りません（必要: 約 {FormatBytes(requiredBytes + floor)}、" +
                $"空き: {FormatBytes(volume.AvailableFreeBytes)}）。");
        }

        return new ArchiveDestinationValidation(
            new ArchiveDestination(normalized, volume, floor, requiredBytes),
            null,
            "保管先の条件を満たしています。");
    }

    /// <summary>
    /// ボリュームで守る空き容量の下限: 警告閾値。一時保管の置き場所と同じボリュームなら、警告閾値と一時保管の
    /// 使用量上限の残り枠の大きい方——保管で一時保管の使える領域を食わないため。
    /// </summary>
    public long FloorFor(ArchiveVolumeInfo volume)
    {
        if (_environment.SpoolDirectory is { } spool
            && _platform.GetVolume(spool) is { } spoolVolume
            && string.Equals(spoolVolume.VolumeRoot, volume.VolumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(_thresholdBytes, _environment.SpoolRemainingQuotaBytes() ?? 0);
        }

        return _thresholdBytes;
    }

    /// <summary>データルートのボリューム（WAL の反映で本体が伸びる側）の情報と下限。</summary>
    public (ArchiveVolumeInfo Volume, long Floor)? DataRootVolume() =>
        _platform.GetVolume(_dataRoot) is { } volume ? (volume, FloorFor(volume)) : null;

    private IEnumerable<(string Path, string Label)> ManagedAreas()
    {
        yield return (_platform.ResolveFinalPath(_dataRoot), "Yagura のデータフォルダ");
        if (_environment.SpoolDirectory is { } spool)
        {
            yield return (_platform.ResolveFinalPath(spool), "一時保管の置き場所");
        }

        yield return (_platform.ResolveFinalPath(_environment.InstallDirectory), "Yagura のインストール先");
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        _ => $"{bytes / 1024.0:F1} KB",
    };

    private static ArchiveDestinationValidation Reject(OldDatabaseDisposalReason reason, string message) =>
        new(null, reason, message);
}
