using System.Runtime.InteropServices;

namespace Yagura.Bench.Reporting;

/// <summary>
/// 実行環境情報（Issue #60「実行環境情報（OS・CPU 名・論理コア数・メモリ量・ディスク種別が
/// 取れる範囲で）を必ず含める」）。数値の出所を追跡可能にするための記録であり、性能公称値の
/// 確定（M7-2）そのものはここでは行わない。
/// </summary>
/// <param name="OsDescription"><see cref="RuntimeInformation.OSDescription"/>。</param>
/// <param name="OsArchitecture"><see cref="RuntimeInformation.OSArchitecture"/>。</param>
/// <param name="ProcessorName">
/// CPU 名（取得できた範囲。Windows は環境変数 <c>PROCESSOR_IDENTIFIER</c> から取得する
/// ——WMI（<c>System.Management</c>）は本ツールの依存を増やすため使わない判断。取得できない
/// 環境では <c>null</c>）。
/// </param>
/// <param name="LogicalProcessorCount"><see cref="Environment.ProcessorCount"/>（論理コア数）。</param>
/// <param name="TotalPhysicalMemoryBytes">
/// 物理メモリ総量（バイト）。<see cref="GC.GetGCMemoryInfo"/> の
/// <c>TotalAvailableMemoryBytes</c> から取得する（.NET 標準 API。コンテナ制限下では
/// コンテナに割り当てられたメモリ量になる——これは「実行環境で実際に使える量」という
/// ベンチの関心に対してむしろ正確）。
/// </param>
/// <param name="DataRootDriveDescription">
/// データルートが乗るドライブの種別情報（取得できた範囲。<see cref="DriveInfo"/> から
/// ドライブ形式・ファイルシステムを取得する——SSD/HDD の物理種別までは .NET 標準 API では
/// 判別できないため、取得できる範囲に留める。取得できない場合は理由を記す）。
/// </param>
/// <param name="DotnetRuntimeVersion"><see cref="RuntimeInformation.FrameworkDescription"/>。</param>
/// <param name="MachineName"><see cref="Environment.MachineName"/>。</param>
public sealed record EnvironmentInfo(
    string OsDescription,
    string OsArchitecture,
    string? ProcessorName,
    int LogicalProcessorCount,
    long TotalPhysicalMemoryBytes,
    string DataRootDriveDescription,
    string DotnetRuntimeVersion,
    string MachineName)
{
    /// <summary>
    /// 現在のプロセスが動作している実行環境の情報を収集する。
    /// </summary>
    /// <param name="dataRoot">ディスク種別情報の収集対象となるデータルートのパス。</param>
    public static EnvironmentInfo Collect(string dataRoot)
    {
        var processorName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");

        var gcInfo = GC.GetGCMemoryInfo();

        string driveDescription;
        try
        {
            var fullPath = Path.GetFullPath(dataRoot);
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                driveDescription = $"{drive.Name} ({drive.DriveType}, {drive.DriveFormat}, " +
                    $"total={drive.TotalSize / (1024 * 1024 * 1024)}GiB, free={drive.AvailableFreeSpace / (1024 * 1024 * 1024)}GiB)";
            }
            else
            {
                driveDescription = "(データルートのドライブルートを解決できなかった)";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            driveDescription = $"(取得不可: {ex.GetType().Name})";
        }

        return new EnvironmentInfo(
            OsDescription: RuntimeInformation.OSDescription,
            OsArchitecture: RuntimeInformation.OSArchitecture.ToString(),
            ProcessorName: processorName,
            LogicalProcessorCount: Environment.ProcessorCount,
            TotalPhysicalMemoryBytes: gcInfo.TotalAvailableMemoryBytes,
            DataRootDriveDescription: driveDescription,
            DotnetRuntimeVersion: RuntimeInformation.FrameworkDescription,
            MachineName: Environment.MachineName);
    }
}
