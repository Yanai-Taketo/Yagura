using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yagura.Storage;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Diagnostics;

namespace Yagura.Web.Tests.ArchitectureTests;

/// <summary>
/// security.md §1 L-5「閲覧リスナに登録される全 HTTP ルート・全ハブが許可リストと一致する」の
/// アーキテクチャテスト（M6-4。Issue #54）。
/// </summary>
/// <remarks>
/// <para>
/// <b>列挙方式</b>: 実サーバは起動するが実際に TCP で listen はしない
/// （<c>ConfigureKestrel(o =&gt; o.Listen(IPAddress.Loopback, 0))</c> で OS 採番の
/// loopback ポートに bind するのみ。E2E のような外部プロセス起動は行わない）。
/// <c>Program.cs</c> と同じ 2 つの集約点
/// （<see cref="YaguraWebViewerExtensions.MapYaguraWebViewer"/> /
/// <see cref="YaguraAdminExtensions.MapYaguraAdmin"/>）を呼び出した後、
/// <c>app.Services.GetServices&lt;EndpointDataSource&gt;()</c> から全登録済みエンドポイントを
/// 取得する。
/// </para>
/// <para>
/// <b>実行して確認した事実（推測ではない）</b>: この方式で
/// <see cref="YaguraWebViewerExtensions.MapYaguraWebViewer"/> が登録する Razor Components の
/// <c>@page</c> ルート（<c>/</c>）と、Interactive Server が自動登録する SignalR ハブ関連の
/// 全ルート（<c>/_blazor</c> 本体・<c>/_blazor/negotiate</c>・<c>/_blazor/disconnect/</c>・
/// <c>/_blazor/initializers/</c>・<c>/_framework/opaque-redirect</c>）が
/// <c>EndpointDataSource</c> の列挙に現れることを実機確認済み（本テストクラスの前身のスパイク
/// テストで確認。この事実自体が <see cref="ViewerAllowlist"/> の妥当性の根拠）。
/// <see cref="YaguraAdminExtensions.MapYaguraAdmin"/> が登録する <c>/admin</c>
/// （<see cref="ListenerPortGuardEndpointMetadata.Admin"/> 付き）も同じ
/// <c>EndpointDataSource</c> に同居して現れる——閲覧許可リストにこれを含めない判定が
/// <see cref="ViewerAllowlistContainsNoAdminEndpoints"/> の役割である。
/// </para>
/// <para>
/// <b>列挙に現れない経路（L-5 覆域の限界。security.md §1 に転記済み）</b>:
/// 一度確立した Blazor Server circuit（<c>/_blazor</c> への WebSocket アップグレード後）上で
/// やり取りされる個々の UI イベント（ボタンクリック等のコンポーネントイベントハンドラ呼び出し）
/// は、ASP.NET Core のルーティング層には現れない別々のエンドポイントではなく、単一の
/// SignalR 接続上でやり取りされるメッセージとして多重化される（Microsoft Learn
/// "ASP.NET Core Blazor hosting models" の Blazor Server 節: "UI updates, event handling,
/// and JavaScript calls are handled over a SignalR connection using the WebSockets protocol."
/// 確認日 2026-07-05）。したがって本テストの列挙は「circuit を確立するまでの経路」を
/// 機械的に検証できるが、「確立した circuit 上で何が呼び出せるか」は対象外であり、
/// その安全性は <see cref="ViewerComponentReferenceIsolationTests"/> の参照分離検査が
/// 別の角度から担保する（構造による担保。security.md §1 の記述のとおり）。
/// </para>
/// <para>
/// <b>「リスト外の経路追加でテストが落ちる」ことの実地確認（PR 記録用）</b>: 実装時に
/// <c>YaguraWebViewerExtensions.MapYaguraWebViewer</c> へ一時的に
/// <c>endpoints.MapGet("/__test-canary", () => "x")</c> を追加し、
/// <see cref="ViewerAllowlist_MatchesAllRegisteredViewerEndpoints"/> が red になることを
/// 確認した上で変更を取り消した（実施記録は本 Issue の PR body に残す）。
/// </para>
/// </remarks>
public sealed class ViewerEndpointAllowlistTests
{
    /// <summary>
    /// 閲覧リスナの全経路許可リスト（読み取り専用の静的な期待値。L-5 の「許可リスト」実体）。
    /// </summary>
    /// <remarks>
    /// 新しい閲覧系ページ・ハブを追加する PR は、本リストの更新を同じ PR に含める必要がある
    /// （更新しなければ <see cref="ViewerAllowlist_MatchesAllRegisteredViewerEndpoints"/> が
    /// 落ちる）。
    /// </remarks>
    private static readonly IReadOnlyList<ExpectedEndpoint> ViewerAllowlist = new List<ExpectedEndpoint>
    {
        // Razor Components の @page ルート（Yagura.Web.Components.Pages。M8-3 で閲覧 3 画面
        // ——ダッシュボード "/"・ログ検索 "/search"・システム状態 "/status"——に拡張。
        // いずれも読み取り専用ページであり、GET/POST は MapRazorComponents の既定登録
        // （POST は Razor Components パイプラインの内部経路で、書き込みエンドポイントの
        // 追加ではない——"/" 1 画面時代からの既存挙動と同一）。
        new("/", new[] { "GET", "POST" }),
        new("/search", new[] { "GET", "POST" }),
        new("/status", new[] { "GET", "POST" }),

        // 接続終了の案内ページ（M8-4。security.md §2.2 の個別切断・SEC-8 の無操作回収の着地先。
        // circuit を要しない静的応答・読み取り専用 GET のみ——書き込みエンドポイントではない）。
        new("/circuit-ended", new[] { "GET" }),

        // Interactive Server（Blazor Server）circuit の確立に必要な SignalR ハブ関連経路。
        // AddInteractiveServerRenderMode が自動登録するもので、Yagura 側では個別に MapHub 等を
        // 呼んでいない——MapRazorComponents().AddInteractiveServerRenderMode() の内部登録の結果を
        // そのまま許可リストへ転記した（推測でなく実機列挙結果）。
        new("/_blazor", null),
        new("/_blazor/negotiate", null),
        new("/_blazor/disconnect/", null),
        new("/_blazor/initializers/", null),

        // Blazor Web App の「不透明リダイレクト」経路（enhanced navigation 用。
        // フレームワーク組み込みのフレームワークアセット配信、書き込みエンドポイントではない）。
        new("/_framework/opaque-redirect", new[] { "GET" }),
    }.AsReadOnly();

    /// <summary>
    /// MudBlazor 同梱静的アセット（M8-1 で MapStaticAssets が登録する読み取り専用経路）の
    /// 制約付き許可パターン。固定リスト（<see cref="ViewerAllowlist"/>）に載せない理由:
    /// MapStaticAssets はフィンガープリント付き経路（例: MudBlazor.min.8fy1no3hvo.css）と
    /// 圧縮変種（.gz / .br。publish 時のみ .br が加わる）を自動生成し、フィンガープリントは
    /// MudBlazor の版更新のたびに変わるため、byte 固定のリストは版更新のたびに無意味な
    /// 改訂を強いる。代わりに「_content/MudBlazor/ 配下の既知 3 アセット
    /// （MudBlazor.min.css / MudBlazor.min.js / MudBlazor.min.js.map）とその
    /// フィンガープリント・圧縮変種のみ」というパターンで突合し、HTTP メソッドは
    /// <see cref="MudBlazorStaticAssets_AreReadOnlyAndConstrainedToKnownAssets"/> で
    /// GET/HEAD に限定されることを検証する（L-5 の趣旨——リスト外の経路追加で落ちる——は
    /// パターン外の静的アセットが追加された時点で本テスト群が落ちることで維持される）。
    /// </summary>
    private static readonly Regex MudBlazorStaticAssetRoutePattern = new(
        "^_content/MudBlazor/MudBlazor\\.min(\\.[0-9a-z]+)?\\.(css|js)(\\.(gz|br))?$" +
        "|^_content/MudBlazor/MudBlazor\\.min\\.js(\\.[0-9a-z]+)?\\.map(\\.(gz|br))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Yagura.Web 自身の静的アセット（M8-2 で追加）の制約付き許可パターン。対象は 2 ファイルのみ:
    /// 共通コンポーネントの内部スタイル（css/yagura-components.css。ui.md §2.1「独自の CSS 変数の
    /// 併設は共通コンポーネントの実装内部に限る」の実装位置）と、ステール警告の自律監視 JS
    /// （js/stale-guard.js。ui.md §5.2 が設計上確定した ADR-0003 決定 1 の例外）。
    /// フィンガープリント・圧縮変種を許容する理由は <see cref="MudBlazorStaticAssetRoutePattern"/> と同じ。
    /// これ以外の自前アセットを追加する PR は、本パターン（と本コメント）の更新を同じ PR に含める。
    /// </summary>
    /// <remarks>
    /// <c>_content/Yagura.Web/</c> プレフィックスを任意一致にしている理由（実機確認済み。2026-07-06）:
    /// 本番経路（Yagura.Host のマニフェスト <c>Yagura.Host.staticwebassets.endpoints.json</c>）では
    /// RCL 規約どおり <c>_content/Yagura.Web/css/yagura-components.css</c> 等で配信される
    /// （YaguraWebApp.razor の link href と一致することをマニフェスト実ファイルで確認）。一方、
    /// 本ハーネスが使う RCL 単位のマニフェスト（<see cref="ViewerHostHarness"/> 参照）では
    /// Yagura.Web 自身がアプリ扱いになり、自前アセットはルート相対
    /// （<c>css/yagura-components.css</c>）で列挙される。両形を許容し、どちらの形でも
    /// 「既知 2 ファイル + 変種のみ・GET/HEAD のみ」の制約は同一に保つ。
    /// </remarks>
    private static readonly Regex YaguraWebStaticAssetRoutePattern = new(
        "^(_content/Yagura\\.Web/)?css/yagura-components(\\.[0-9a-z]+)?\\.css(\\.(gz|br))?$" +
        "|^(_content/Yagura\\.Web/)?js/stale-guard(\\.[0-9a-z]+)?\\.js(\\.(gz|br))?$",
        RegexOptions.Compiled);

    [Fact]
    public async Task ViewerAllowlist_MatchesAllRegisteredViewerEndpoints()
    {
        await using var harness = await ViewerHostHarness.StartAsync();

        var actual = harness.GetViewerEndpoints()
            .Where(e => !IsStaticAssetRoute(e))
            .Select(e => new ExpectedEndpoint(e.RoutePattern.RawText ?? string.Empty, GetMethods(e)))
            .OrderBy(e => e.RoutePattern, StringComparer.Ordinal)
            .ToList();

        var expected = ViewerAllowlist
            .OrderBy(e => e.RoutePattern, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task MudBlazorStaticAssets_AreReadOnlyAndConstrainedToKnownAssets()
    {
        // M8-1(Issue #68): MapStaticAssets が登録する MudBlazor 同梱アセットの経路が
        // (a) 既知 3 アセット(+フィンガープリント・圧縮変種)のパターンに完全一致し、
        // (b) HTTP メソッドが GET/HEAD のみ(読み取り専用——ui.md §4 の「閲覧リスナは
        //     いかなる書き込みエンドポイントも持たない」不変条件)であることを検証する。
        await using var harness = await ViewerHostHarness.StartAsync();

        var staticAssetEndpoints = harness.GetViewerEndpoints()
            .Where(IsStaticAssetRoute)
            .ToList();

        // 既知アセットが最低 1 経路ずつ現れること(検出対象が空集合のまま green になる
        // 空虚な真を避ける——本クラスの他テストと同じ注意)。
        Assert.Contains(staticAssetEndpoints, e => NormalizeRoute(e) == "_content/MudBlazor/MudBlazor.min.css");
        Assert.Contains(staticAssetEndpoints, e => NormalizeRoute(e) == "_content/MudBlazor/MudBlazor.min.js");

        // M8-2 で追加した Yagura.Web 自前アセット 2 ファイル(共通コンポーネント CSS +
        // ステール警告 JS)も配信面に現れること(ルート形はハーネスではルート相対——
        // YaguraWebStaticAssetRoutePattern の remarks 参照)。
        Assert.Contains(staticAssetEndpoints, e => NormalizeRoute(e).EndsWith("css/yagura-components.css", StringComparison.Ordinal));
        Assert.Contains(staticAssetEndpoints, e => NormalizeRoute(e).EndsWith("js/stale-guard.js", StringComparison.Ordinal));

        foreach (var endpoint in staticAssetEndpoints)
        {
            var route = NormalizeRoute(endpoint);
            Assert.True(
                MudBlazorStaticAssetRoutePattern.IsMatch(route) || YaguraWebStaticAssetRoutePattern.IsMatch(route),
                $"許可パターン外の静的アセット経路が閲覧リスナに追加されている: {route}(L-5。" +
                "追加する場合は本テストの許可パターンの更新を同じ PR に含める)");

            var methods = GetMethods(endpoint);
            Assert.NotNull(methods);
            Assert.All(methods, m => Assert.Contains(m, new[] { "GET", "HEAD" }));
        }
    }

    /// <summary>
    /// 静的アセット経路の判定（<c>_content/</c> 配下——Razor Class Library / NuGet 同梱
    /// アセットの規約プレフィックス——または Yagura.Web 自前アセットのルート相対形。
    /// 後者はハーネスの RCL 単位マニフェストでのみ現れる——
    /// <see cref="YaguraWebStaticAssetRoutePattern"/> の remarks 参照）。
    /// 固定許可リストとパターン許可リストの分割点。
    /// </summary>
    private static bool IsStaticAssetRoute(RouteEndpoint endpoint)
    {
        var route = NormalizeRoute(endpoint);
        return route.StartsWith("_content/", StringComparison.Ordinal) ||
               YaguraWebStaticAssetRoutePattern.IsMatch(route);
    }

    private static string NormalizeRoute(RouteEndpoint endpoint) =>
        (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');

    [Fact]
    public async Task ViewerAllowlistContainsNoAdminEndpoints()
    {
        // security.md §3「監査記録の閲覧は管理役割・管理リスナ帰属」・ui.md §4「閲覧リスナは
        // いかなる書き込みエンドポイントも持たない」の許可リスト側の裏付け:
        // ListenerPortGuardEndpointMetadata.Admin を持つエンドポイントは閲覧許可リストに
        // 一切現れないことを確認する（本許可リストの構造上の不変条件）。
        await using var harness = await ViewerHostHarness.StartAsync();

        var adminEndpointRoutes = harness.GetAllEndpoints()
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ListenerPortGuardEndpointMetadata>() is { Kind: ListenerKind.Admin })
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.All(adminEndpointRoutes, adminRoute =>
            Assert.DoesNotContain(ViewerAllowlist, e => e.RoutePattern == adminRoute));

        // 逆方向: 管理画面 5 ページ(M8-4 の 4 ページ + ADR-0008 の /admin/forwarder-kit。
        // Yagura.Web.Administration.Screens 配下)が実際に Admin メタデータ付きで存在することも
        // 確認する（「管理系が 0 件だから許可リストに含まれない」という空虚な真になっていない
        // ことの保証。MapYaguraAdmin の名前空間由来の機械的付与——convention——が実際に
        // 効いていることの検証でもある）。
        Assert.Contains("/admin", adminEndpointRoutes);
        Assert.Contains("/admin/setup", adminEndpointRoutes);
        Assert.Contains("/admin/promotion", adminEndpointRoutes);
        Assert.Contains("/admin/circuits", adminEndpointRoutes);
        Assert.Contains("/admin/forwarder-kit", adminEndpointRoutes);

        // /admin/forwarder-kit/download は Razor Components のページではなく素の MapGet
        // であり、名前空間由来の自動付与の対象外——YaguraAdminExtensions.MapForwarderKitDownload
        // が明示的に Admin メタデータを付与していることを確認する（ADR-0008 設計条件 7）。
        Assert.Contains("/admin/forwarder-kit/download", adminEndpointRoutes);
    }

    [Fact]
    public async Task AdminEndpoints_AreReachableFromCombinedHost_ButNotPartOfViewerAllowlist()
    {
        // M6-1（PR #55）の決定「閲覧系ルートは管理リスナからも到達できる」の裏返し確認:
        // 同一の EndpointDataSource に閲覧・管理の両ルートが同居する構造そのものは正しい
        // （両者は同じ IEndpointRouteBuilder にマップされる——ポートによる到達可否の分離は
        // ListenerPortGuardMiddleware が実行時に担う。ListenerPortGuardMiddlewareTests 参照）。
        // 本許可リストは「閲覧リスナの視点で許可された経路」を表現するものであり、
        // /admin はここでは意図的に対象外とする。
        await using var harness = await ViewerHostHarness.StartAsync();

        var allRouteTexts = harness.GetAllEndpoints()
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.Contains("/admin", allRouteTexts);
        Assert.DoesNotContain(ViewerAllowlist, e => e.RoutePattern == "/admin");
    }

    private static string[]? GetMethods(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return methods?.ToArray();
    }

    /// <summary>許可リストの 1 行（ルートパターンの生テキスト + 許容 HTTP メソッド）。</summary>
    private sealed record ExpectedEndpoint(string RoutePattern, string[]? Methods)
    {
        public bool Equals(ExpectedEndpoint? other)
        {
            if (other is null)
            {
                return false;
            }

            if (RoutePattern != other.RoutePattern)
            {
                return false;
            }

            if (Methods is null || other.Methods is null)
            {
                return Methods is null && other.Methods is null;
            }

            return Methods.OrderBy(m => m, StringComparer.Ordinal)
                .SequenceEqual(other.Methods.OrderBy(m => m, StringComparer.Ordinal), StringComparer.Ordinal);
        }

        public override int GetHashCode() => RoutePattern.GetHashCode(StringComparison.Ordinal);
    }
}
