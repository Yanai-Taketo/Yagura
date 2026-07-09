using Yagura.Web.Tests.ArchitectureTests;

namespace Yagura.Web.Tests;

/// <summary>
/// <c>/health</c>（閲覧リスナの liveness エンドポイント）の実 HTTP 応答テスト
/// （Issue #126。2026-07-09 オーナー決定: 採用。認証なし・固定レスポンス限定）。
/// </summary>
/// <remarks>
/// <see cref="ViewerHostHarness"/>（L-5 アーキテクチャテスト用ハーネス）を流用する——同ハーネスは
/// 実際に Kestrel を loopback + OS 採番ポートで起動するため（<c>StartAsync</c> 済み）、
/// 本テストは実 HTTP リクエストで応答を検証できる。ルート表への登録自体（許可リスト突合）は
/// <c>ViewerEndpointAllowlistTests</c> の管轄——本テストは実応答（ステータス・本文・DB 非依存）を
/// 確認する。
/// </remarks>
public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Health_Returns200WithFixedBody()
    {
        await using var harness = await ViewerHostHarness.StartAsync();
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("OK", body);
    }

    [Fact]
    public async Task Health_PostIsNotAllowed()
    {
        // /health は GET のみ登録（MapGet）——書き込みエンドポイントではないことの実地確認
        // （ui.md §4「閲覧リスナはいかなる書き込みエンドポイントも持たない」の裏付け。
        // ViewerEndpointAllowlistTests の許可リスト側の期待値と対をなす実 HTTP 確認）。
        await using var harness = await ViewerHostHarness.StartAsync();
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.PostAsync("/health", content: null);

        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
