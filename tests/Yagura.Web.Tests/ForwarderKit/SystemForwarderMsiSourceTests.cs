using System.Runtime.Versioning;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// <see cref="SystemForwarderMsiSource"/> の実ファイルシステムに対する境界テスト
/// （ADR-0008 設計条件 9・ADR-0009 決定7・委任 #4）。
/// </summary>
/// <remarks>
/// 主眼は「配置フォルダに win64・ARM64 の MSI が混在していても、<see cref="IForwarderMsiSource.Lookup"/>
/// はアーキごとに独立して検出する」という ADR-0009 決定7・委任 #4 の設計を固定すること
/// ——<see cref="ForwarderMsiLookup.State"/> が <see cref="ForwarderMsiLookupState.Multiple"/> に
/// ならないことを実ファイルシステム越しに確認する（<see cref="ForwarderMsiFilterTests"/> は
/// 判定ロジック単体のみを検証しており、列挙側の結線はここで確認する）。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SystemForwarderMsiSourceTests : IDisposable
{
    private readonly string _folder;

    public SystemForwarderMsiSourceTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"yagura-forwarder-msi-source-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Lookup_FolderMissing_ReturnsNotFound()
    {
        var missingFolder = Path.Combine(_folder, "does-not-exist");
        var source = new SystemForwarderMsiSource(missingFolder);

        Assert.Equal(ForwarderMsiLookupState.NotFound, source.Lookup(ForwarderMsiArchitecture.Win64).State);
        Assert.Equal(ForwarderMsiLookupState.NotFound, source.Lookup(ForwarderMsiArchitecture.WinArm64).State);
    }

    [Fact]
    public void Lookup_EmptyFolder_ReturnsNotFoundForBothArchitectures()
    {
        var source = new SystemForwarderMsiSource(_folder);

        Assert.Equal(ForwarderMsiLookupState.NotFound, source.Lookup(ForwarderMsiArchitecture.Win64).State);
        Assert.Equal(ForwarderMsiLookupState.NotFound, source.Lookup(ForwarderMsiArchitecture.WinArm64).State);
    }

    [Fact]
    public void Lookup_MixedArchitectureFolder_EachArchitectureSeesOnlyItsOwnFile()
    {
        // 混在は「誤配布」ではなく正常な状態でありうる（収集対象端末のアーキが異なる複数の
        // 端末群を同じサーバから配布する運用。ADR-0009 決定7・委任 #4）。
        WriteDummyFile("fluent-bit-5.0.8-win64.msi");
        WriteDummyFile("fluent-bit-5.0.8-winarm64.msi");

        var source = new SystemForwarderMsiSource(_folder);

        var win64Lookup = source.Lookup(ForwarderMsiArchitecture.Win64);
        Assert.Equal(ForwarderMsiLookupState.Single, win64Lookup.State);
        Assert.Equal("fluent-bit-5.0.8-win64.msi", win64Lookup.Details!.FileName);

        var arm64Lookup = source.Lookup(ForwarderMsiArchitecture.WinArm64);
        Assert.Equal(ForwarderMsiLookupState.Single, arm64Lookup.State);
        Assert.Equal("fluent-bit-5.0.8-winarm64.msi", arm64Lookup.Details!.FileName);
    }

    [Fact]
    public void Lookup_TwoWin64FilesOneWinArm64File_Win64IsMultipleArm64IsSingle()
    {
        WriteDummyFile("fluent-bit-4.0.14-win64.msi");
        WriteDummyFile("fluent-bit-5.0.8-win64.msi");
        WriteDummyFile("fluent-bit-5.0.8-winarm64.msi");

        var source = new SystemForwarderMsiSource(_folder);

        Assert.Equal(ForwarderMsiLookupState.Multiple, source.Lookup(ForwarderMsiArchitecture.Win64).State);
        Assert.Equal(ForwarderMsiLookupState.Single, source.Lookup(ForwarderMsiArchitecture.WinArm64).State);
    }

    [Fact]
    public void Lookup_ComputesSha256AndLength()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var path = Path.Combine(_folder, "fluent-bit-5.0.8-winarm64.msi");
        File.WriteAllBytes(path, bytes);

        var source = new SystemForwarderMsiSource(_folder);
        var lookup = source.Lookup(ForwarderMsiArchitecture.WinArm64);

        Assert.Equal(ForwarderMsiLookupState.Single, lookup.State);
        Assert.Equal(bytes.Length, lookup.Details!.Length);
        Assert.Equal(64, lookup.Details.Sha256.Length);
        Assert.Equal(lookup.Details.Sha256, lookup.Details.Sha256.ToLowerInvariant());
    }

    private void WriteDummyFile(string fileName) =>
        File.WriteAllBytes(Path.Combine(_folder, fileName), [1, 2, 3, 4, 5]);
}
