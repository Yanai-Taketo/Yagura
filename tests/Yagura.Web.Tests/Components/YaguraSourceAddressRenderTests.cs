using Microsoft.Extensions.DependencyInjection;
using Yagura.Web.Components.Common;
using Yagura.Web.ReverseDns;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// <see cref="YaguraSourceAddress"/> の表示確認（ADR-0007 決定 1。ui.md §4——IP が主・
/// 逆引き名が補助・由来ツールチップ・引けない場合は IP のみ）。
/// </summary>
public sealed class YaguraSourceAddressRenderTests
{
    private sealed class FakeResolver(string? name) : IReverseDnsResolver
    {
        public string? TryGetDisplayName(string sourceAddress) => name;

#pragma warning disable CS0067 // 描画テストでは発火しない（購読解除は Dispose 検証の対象外）
        public event Action? NamesUpdated;
#pragma warning restore CS0067
    }

    private static Task<string> RenderAsync(string address, string? resolvedName, int? port = null)
    {
        var parameters = new Dictionary<string, object?>
        {
            [nameof(YaguraSourceAddress.Address)] = address,
            [nameof(YaguraSourceAddress.Port)] = port,
        };

        return CommonComponentRenderHarness.RenderAsync<YaguraSourceAddress>(
            parameters,
            services => services.AddSingleton<IReverseDnsResolver>(new FakeResolver(resolvedName)));
    }

    [Fact]
    public async Task WithResolvedName_ShowsIpAsPrimaryAndNameAsSecondaryWithTooltip()
    {
        var html = await RenderAsync("192.168.1.10", "printer-01.corp.example");

        // IP は等幅で常に表示（主）・逆引き名は補助クラスで併記（ui.md §4）。
        Assert.Contains("yagura-monospace", html);
        Assert.Contains("192.168.1.10", html);
        Assert.Contains("yagura-reverse-dns", html);
        Assert.Contains("printer-01.corp.example", html);
        // 由来のツールチップ（確定文言 = UiText.ReverseDnsTooltip。3 箇所共通で本部品が内蔵）。
        Assert.Contains(UiText.ReverseDnsTooltip, html);
    }

    [Fact]
    public async Task WithoutResolvedName_ShowsIpOnlyWithoutTooltip()
    {
        var html = await RenderAsync("10.0.0.1", resolvedName: null);

        // 引けない場合は IP のみ＝表示の欠落を異常に見せない（ui.md §4）。
        Assert.Contains("10.0.0.1", html);
        Assert.DoesNotContain("yagura-reverse-dns", html);
        Assert.DoesNotContain(UiText.ReverseDnsTooltip, html);
    }

    [Fact]
    public async Task WithPort_ShowsAddressPortPair()
    {
        var html = await RenderAsync("10.0.0.1", resolvedName: null, port: 514);

        Assert.Contains("10.0.0.1:514", html);
    }
}
