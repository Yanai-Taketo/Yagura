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
    public async Task Health_HeadReturns200WithoutBody()
    {
        // 外形監視・LB ツールには帯域節約のため既定で HEAD を送るものがあり、GET のみでは
        // 本エンドポイントの目的（外形監視からの死活確認）が一部ツール構成で達成できない
        // （PR #164 レビュー指摘）。HTTP セマンティクス上 HEAD は「GET と同一ヘッダ・本文なし」
        // ——200 + 本文が空であることを実 HTTP で確認する。
        await using var harness = await ViewerHostHarness.StartAsync();
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        using var request = new HttpRequestMessage(HttpMethod.Head, "/health");
        var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task Health_PostIsNotAllowed()
    {
        // /health は GET/HEAD のみ登録——書き込みエンドポイントではないことの実地確認
        // （ui.md §4「閲覧リスナはいかなる書き込みエンドポイントも持たない」の裏付け。
        // ViewerEndpointAllowlistTests の許可リスト側の期待値と対をなす実 HTTP 確認）。
        await using var harness = await ViewerHostHarness.StartAsync();
        using var client = new HttpClient { BaseAddress = harness.GetBaseAddress() };

        var response = await client.PostAsync("/health", content: null);

        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
