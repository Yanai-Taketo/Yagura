using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Routing;
using Yagura.Abstractions.Administration;
using Yagura.Web.Administration.Screens;

namespace Yagura.Web.Tests.ArchitectureTests;

/// <summary>
/// 管理画面（M8-4。Issue #71）の構造検査。
/// </summary>
/// <remarks>
/// 検証する不変条件:
/// <list type="number">
/// <item>管理画面（<c>Yagura.Web.Administration</c> 名前空間のページ）のエンドポイントはすべて
/// <see cref="ListenerPortGuardEndpointMetadata.Admin"/> を持つ（<c>MapYaguraAdmin</c> の
/// 名前空間由来の機械的付与が全ページに効いている——1 ページの漏れが閲覧リスナへの
/// 管理画面露出になるため、列挙で機械検証する）。</item>
/// <item>ルートを持つ管理画面はすべて <see cref="AdminScreenLayout"/> を経由する（circuit 層の
/// リスナ帰属検査——security.md §1 L-5 の覆域の限界を埋める二層目——を素通りするページを
/// 作れないようにする）。</item>
/// <item><see cref="IYaguraWriteService"/> の実装が実在する（M6-4 の申し送り「検査の実効化」——
/// <see cref="ViewerComponentReferenceIsolationTests"/> が空集合に対する空虚な真で green に
/// なっていないことの裏付け）。</item>
/// </list>
/// </remarks>
public sealed class AdminScreenArchitectureTests
{
    [Fact]
    public async Task AdminScreenPageEndpoints_AllCarryAdminListenerMetadata()
    {
        await using var harness = await ViewerHostHarness.StartAsync();

        var adminPageEndpoints = harness.GetAllEndpoints()
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ComponentTypeMetadata>()?.Type.Namespace is string ns &&
                        (ns == YaguraAdminExtensions.AdminScreenNamespacePrefix ||
                         ns.StartsWith(YaguraAdminExtensions.AdminScreenNamespacePrefix + ".", StringComparison.Ordinal)))
            .ToList();

        // 空虚な真の防止: 管理画面のページエンドポイントが実在すること（M8-4 で 4 画面）。
        Assert.NotEmpty(adminPageEndpoints);

        Assert.All(adminPageEndpoints, endpoint =>
            Assert.True(
                endpoint.Metadata.GetMetadata<ListenerPortGuardEndpointMetadata>() is { Kind: ListenerKind.Admin },
                $"管理画面のエンドポイント {endpoint.RoutePattern.RawText} に Admin メタデータが付与されていない" +
                "（MapYaguraAdmin の convention が効いていない——閲覧リスナへ管理画面が露出する）。"));
    }

    [Fact]
    public void RoutableAdminScreens_AllUseAdminScreenLayout()
    {
        var adminScreenPages = typeof(YaguraWebViewerExtensions).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.Namespace == typeof(AdminScreenLayout).Namespace)
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any())
            .ToList();

        Assert.NotEmpty(adminScreenPages);

        Assert.All(adminScreenPages, page =>
            Assert.True(
                page.GetCustomAttribute<LayoutAttribute>()?.LayoutType == typeof(AdminScreenLayout),
                $"管理画面 {page.FullName} が AdminScreenLayout を経由していない" +
                "（circuit 層のリスナ帰属検査を素通りする——@layout AdminScreenLayout を指定すること）。"));
    }

    [Fact]
    public void WriteServiceImplementations_Exist_SoIsolationTestIsEffective()
    {
        // M6-4 申し送りの決着（M8-4）: IYaguraWriteService を実装する契約・実装が実在し、
        // ViewerComponentReferenceIsolationTests の検査が実効化していることを機械的に固定する。
        var contracts = new[]
        {
            typeof(ISetupWizardService),
            typeof(IPromotionWizardService),
            typeof(ICircuitManagementService),
        };

        Assert.All(contracts, contract =>
            Assert.True(
                typeof(IYaguraWriteService).IsAssignableFrom(contract),
                $"{contract.FullName} は IYaguraWriteService を実装していない（書き込み系契約の申告漏れ）。"));

        // Yagura.Web アセンブリ内の具体実装（circuit 管理）が存在すること。ウィザード系の
        // 具体実装は Yagura.Host 側にあり本テストの参照範囲外だが、契約レベルの申告（上記）で
        // 参照分離検査は成立する（検査は注入される「型」——通常は契約インターフェース——を見る）。
        var webImplementations = typeof(YaguraWebViewerExtensions).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IYaguraWriteService).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(webImplementations);
    }
}
