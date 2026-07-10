using System.IO.Compression;
using Yagura.Web.ForwarderKit;
using Yagura.Web.Tests.ArchitectureTests;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// <c>/admin/forwarder-kit/download</c> の実 HTTP 応答テスト（ADR-0008 設計条件 7・9・委任 #5・#7。
/// 正常系 = 200 + application/zip、検証失敗 = 400、MSI 同梱要求の各検出状態を確認する）。
/// </summary>
/// <remarks>
/// <see cref="ViewerHostHarness"/>（L-5 アーキテクチャテスト用ハーネス）を流用する——同ハーネスは
/// 実際に Kestrel を loopback + OS 採番ポートで起動するため（<c>StartAsync</c> 済み）、
/// 本テストは実 HTTP リクエストで応答を検証できる。管理リスナからの到達可否のポート判定
/// （非 loopback からの拒否等）は <c>ListenerPortGuardMiddlewareTests</c> の管轄であり、
/// 本テストの対象外——ここではエンドポイントの検証・生成ロジックの結線のみを確認する。
/// </remarks>
public sealed class ForwarderKitDownloadEndpointTests
{
    [Fact]
    public async Task Download_ValidRequest_Returns200WithZipContent()
    {
        await using var harness = await ViewerHostHarness.StartAsync();
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/admin/forwarder-kit/download?host=192.0.2.10&port=514&channels=System,Application");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty);
        Assert.Contains("no-msi.zip", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(6, archive.Entries.Count);
    }

    [Fact]
    public async Task Download_MissingHost_Returns400()
    {
        await using var harness = await ViewerHostHarness.StartAsync();
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/admin/forwarder-kit/download?port=514");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Download_InvalidPort_Returns400()
    {
        await using var harness = await ViewerHostHarness.StartAsync();
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/admin/forwarder-kit/download?host=192.0.2.10&port=99999");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Download_UnknownChannel_Returns400()
    {
        await using var harness = await ViewerHostHarness.StartAsync();
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/admin/forwarder-kit/download?host=192.0.2.10&port=514&channels=Bogus");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- MSI オプトイン同梱（ADR-0008 設計条件 9） ----

    [Fact]
    public async Task Download_IncludeMsiTrue_NotFound_Returns400()
    {
        // 既定のハーネス構成（StubForwarderMsiSource）は常に未検出を返す。
        await using var harness = await ViewerHostHarness.StartAsync();
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/admin/forwarder-kit/download?host=192.0.2.10&port=514&channels=System&includeMsi=true");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Download_IncludeMsiTrue_Multiple_Returns400()
    {
        var msiSource = new FakeForwarderMsiSource(
            @"C:\ProgramData\Yagura\forwarder",
            ForwarderMsiLookup.Multiple(["fluent-bit-4.0.14-win64.msi", "fluent-bit-4.0.13-win64.msi"]));

        await using var harness = await ViewerHostHarness.StartAsync(msiSource);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/admin/forwarder-kit/download?host=192.0.2.10&port=514&channels=System&includeMsi=true");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Download_IncludeMsiTrue_SingleMatchingVersion_Returns200WithMsiEntry()
    {
        var msiFileName = "fluent-bit-" + ForwarderKitConstraints.VerifiedFluentBitVersion + "-win64.msi";
        var tempPath = CreateTempMsiFile(msiFileName);

        var msiSource = new FakeForwarderMsiSource(
            Path.GetDirectoryName(tempPath)!,
            ForwarderMsiLookup.Single(new ForwarderMsiDetails(
                tempPath,
                msiFileName,
                ForwarderKitConstraints.VerifiedFluentBitVersion,
                "dummy-sha",
                5)));

        await using var harness = await ViewerHostHarness.StartAsync(msiSource);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/admin/forwarder-kit/download?host=192.0.2.10&port=514&channels=System&includeMsi=true");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("with-msi.zip", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry(msiFileName));
    }

    [Fact]
    public async Task Download_IncludeMsiTrue_VersionMismatchNotAcknowledged_Returns400()
    {
        var msiFileName = "fluent-bit-0.0.1-win64.msi";
        var tempPath = CreateTempMsiFile(msiFileName);

        var msiSource = new FakeForwarderMsiSource(
            Path.GetDirectoryName(tempPath)!,
            ForwarderMsiLookup.Single(new ForwarderMsiDetails(tempPath, msiFileName, "0.0.1", "dummy-sha", 5)));

        await using var harness = await ViewerHostHarness.StartAsync(msiSource);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/admin/forwarder-kit/download?host=192.0.2.10&port=514&channels=System&includeMsi=true");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Download_IncludeMsiTrue_VersionMismatchAcknowledged_Returns200()
    {
        var msiFileName = "fluent-bit-0.0.1-win64.msi";
        var tempPath = CreateTempMsiFile(msiFileName);

        var msiSource = new FakeForwarderMsiSource(
            Path.GetDirectoryName(tempPath)!,
            ForwarderMsiLookup.Single(new ForwarderMsiDetails(tempPath, msiFileName, "0.0.1", "dummy-sha", 5)));

        await using var harness = await ViewerHostHarness.StartAsync(msiSource);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync(
            "/admin/forwarder-kit/download?host=192.0.2.10&port=514&channels=System&includeMsi=true&msiVersionMismatchAcknowledged=true");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    // ---- アーキ選択（ADR-0009 決定7・委任 #4） ----

    [Fact]
    public async Task Download_IncludeMsiTrue_ArchitectureArm64_SingleMatchingVersion_Returns200WithMsiEntry()
    {
        var msiFileName = "fluent-bit-" + ForwarderKitConstraints.VerifiedFluentBitVersion + "-winarm64.msi";
        var tempPath = CreateTempMsiFile(msiFileName);

        var msiSource = new FakeForwarderMsiSource(
            Path.GetDirectoryName(tempPath)!,
            ForwarderMsiLookup.Single(new ForwarderMsiDetails(
                tempPath,
                msiFileName,
                ForwarderKitConstraints.VerifiedFluentBitVersion,
                "dummy-sha",
                5)));

        await using var harness = await ViewerHostHarness.StartAsync(msiSource);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync(
            "/admin/forwarder-kit/download?host=192.0.2.10&port=514&channels=System&includeMsi=true&architecture=arm64");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("with-msi.zip", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry(msiFileName));
    }

    [Fact]
    public async Task Download_IncludeMsiTrue_InvalidArchitecture_Returns400()
    {
        var msiSource = new FakeForwarderMsiSource(
            @"C:\ProgramData\Yagura\forwarder",
            ForwarderMsiLookup.NotFound());

        await using var harness = await ViewerHostHarness.StartAsync(msiSource);
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync(
            "/admin/forwarder-kit/download?host=192.0.2.10&port=514&channels=System&includeMsi=true&architecture=mips");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string CreateTempMsiFile(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"yagura-forwarder-msi-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        return path;
    }

    private sealed class FakeForwarderMsiSource(string folderPath, ForwarderMsiLookup lookup) : IForwarderMsiSource
    {
        public string FolderPath => folderPath;

        public ForwarderMsiLookup Lookup(ForwarderMsiArchitecture architecture) => lookup;
    }
}
