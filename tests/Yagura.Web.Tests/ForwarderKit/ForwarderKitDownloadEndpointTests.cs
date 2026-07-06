using System.IO.Compression;
using Yagura.Web.Tests.ArchitectureTests;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// <c>/admin/forwarder-kit/download</c> の実 HTTP 応答テスト（ADR-0008 設計条件 7・委任 #5。
/// 正常系 = 200 + application/zip、検証失敗 = 400 を確認する）。
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
}
