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
    public static async Task<string> RenderAsync<TComponent>(Dictionary<string, object?>? parameters = null)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // MudBlazor コンポーネントが注入する各種サービス。JS 依存サービスには何もしない
        // スタブ IJSRuntime を与える（初期描画は JS を呼ばない——prerender 安全性の前提）。
        services.AddSingleton<IJSRuntime>(new StubJSRuntime());
        services.AddMudServices();

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

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();

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

    private sealed class StubJSRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => ValueTask.FromResult(default(TValue)!);
    }
}
