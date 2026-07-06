using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// 共通コンポーネントの表示確認ハーネス（M8-2。Issue #69）: フレームワーク標準の
/// <see cref="HtmlRenderer"/> でコンポーネントを実際に HTML へ描画し、マークアップ・文言を
/// 検証する。
/// </summary>
/// <remarks>
/// <para>
/// <b>bUnit 等の外部テストライブラリを追加しなかった理由</b>: .NET 8 以降は
/// <c>Microsoft.AspNetCore.Components.Web.HtmlRenderer</c>（フレームワーク同梱・public API）で
/// コンポーネントの実描画が得られ、本マイルストーンの検証対象（初期描画のマークアップ構造・
/// 文言・aria 属性）には十分である（conventions.md「依存を増やさない」の判断規準。
/// ViewerComponentReferenceIsolationTests が NetArchTest を避けたのと同じ向き）。
/// クリック後の状態遷移など対話的検証が必要になった時点で bUnit の採用を再検討する。
/// </para>
/// <para>
/// <b>描画は prerender 相当</b>: <see cref="HtmlRenderer"/> は初期描画のみを行い、
/// <c>OnAfterRenderAsync</c> は呼ばれない——JS interop を初回描画後に行う部品
/// （YaguraStaleGuard 等）も JS なしで描画できる（Interactive Server の prerender と
/// 同じ性質。ADR-0003 決定 1）。
/// </para>
/// </remarks>
internal static class CommonComponentRenderHarness
{
    /// <summary>コンポーネントを描画して HTML 文字列を返す。</summary>
    /// <param name="parameters">コンポーネントへ渡すパラメータ。</param>
    /// <param name="configureServices">
    /// 追加のサービス登録（M8-3: 画面コンポーネントが注入する ILogStore・
    /// IYaguraSystemStatusReader 等のフェイクを利用側テストが登録する差し込み口）。
    /// </param>
    /// <param name="includePopoverProvider">
    /// MudPopoverProvider を同居させるか（既定 true）。MainLayout のように検証対象自身が
    /// プロバイダ群を内包するコンポーネントは false にする（二重登録は
    /// SectionRegistry の重複購読エラーになる——実挙動で確認済み）。
    /// </param>
    public static async Task<string> RenderAsync<TComponent>(
        Dictionary<string, object?>? parameters = null,
        Action<IServiceCollection>? configureServices = null,
        bool includePopoverProvider = true)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // MudBlazor コンポーネントが注入する各種サービス。JS 依存サービスには何もしない
        // スタブ IJSRuntime を与える（初期描画は JS を呼ばない——prerender 安全性の前提）。
        services.AddSingleton<IJSRuntime>(new StubJSRuntime());
        services.AddMudServices();

        // ナビゲーション系コンポーネント（MudNavLink・MudLink 等）が要求する NavigationManager
        // （HtmlRenderer には既定登録がないため、固定 URI のテスト実装を与える）。
        services.AddSingleton<NavigationManager>(new TestNavigationManager());

        // 逆引きホスト名表示（ADR-0007。YaguraSourceAddress・SystemStatus が注入する）。
        // 描画テストは常に無効構成で行い、外向き DNS クエリを発しない（決定 4 の縮小側と同じ向き。
        // 有効時の解決挙動は ReverseDnsResolverTests が偽実装で検証する）。
        services.AddSingleton(new Yagura.Web.ReverseDns.ReverseDnsDisplayOptions(Enabled: false));
        services.AddSingleton<Yagura.Web.ReverseDns.IReverseDnsLookup, Yagura.Web.ReverseDns.SystemDnsReverseLookup>();
        services.AddSingleton<Yagura.Web.Diagnostics.ReverseDnsMetrics>();
        services.AddSingleton<Yagura.Web.ReverseDns.IReverseDnsResolver, Yagura.Web.ReverseDns.ReverseDnsResolver>();
        services.AddSingleton(TimeProvider.System);

        configureServices?.Invoke(services);

        await using var provider = services.BuildServiceProvider();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        await using var renderer = new HtmlRenderer(provider, loggerFactory);

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            // MudPopoverProvider を同居させる（MainLayout と同じ構成——MudTablePager 等の
            // ポップオーバー内蔵部品が初期描画時に要求する）。
            var hostParameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(ProviderHost.ComponentType)] = typeof(TComponent),
                [nameof(ProviderHost.ComponentParameters)] = parameters,
                [nameof(ProviderHost.IncludePopoverProvider)] = includePopoverProvider,
            });
            var output = await renderer.RenderComponentAsync<ProviderHost>(hostParameters);
            return output.ToHtmlString();
        });

        // HtmlRenderer は非 ASCII 文字を数値文字参照（&#x...;）へエンコードして出力するため、
        // 日本語文言の突合のためにデコードして返す（マークアップ構造の検証には影響しない）。
        return System.Net.WebUtility.HtmlDecode(html);
    }

    /// <summary>文字列を子要素として描画する RenderFragment を作る。</summary>
    public static RenderFragment Text(string text) => builder => builder.AddContent(0, text);

    /// <summary>
    /// 検証対象コンポーネントを MudBlazor のプロバイダと同居させて描画するホスト
    /// （実アプリの MainLayout に相当する最小構成）。
    /// </summary>
    private sealed class ProviderHost : ComponentBase
    {
        [Parameter]
        public Type ComponentType { get; set; } = default!;

        [Parameter]
        public Dictionary<string, object?>? ComponentParameters { get; set; }

        [Parameter]
        public bool IncludePopoverProvider { get; set; } = true;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            if (IncludePopoverProvider)
            {
                builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
                builder.CloseComponent();
            }

            builder.OpenComponent(1, ComponentType);
            if (ComponentParameters is not null)
            {
                builder.AddMultipleAttributes(
                    2,
                    ComponentParameters.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value!)));
            }

            builder.CloseComponent();
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/");
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            // 描画テストでは遷移しない。
        }
    }

    private sealed class StubJSRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => ValueTask.FromResult(default(TValue)!);
    }
}
