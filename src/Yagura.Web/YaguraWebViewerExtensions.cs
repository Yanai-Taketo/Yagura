using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using Yagura.Storage;
using Yagura.Web.Administration;
using Yagura.Web.Circuits;
using Yagura.Web.Components;
using Yagura.Web.Components.Common;
using Yagura.Web.Export;

namespace Yagura.Web;

/// <summary>
/// Yagura.Web（閲覧リスナ）を Host から組み込むための拡張メソッド。
/// </summary>
/// <remarks>
/// <b>ルート登録は本クラスの <see cref="MapYaguraWebViewer"/> の 1 箇所に集約する</b>。
/// 閲覧リスナは書き込みエンドポイントを持たず（architecture.md §6・ui.md §4 の不変条件）、
/// security.md §1 L-5 の「全ルート許可リスト」アーキテクチャテスト
/// （<c>ViewerEndpointAllowlistTests</c>）がこの集約点を検査対象にする。新しい閲覧系ページを
/// 追加する場合も、ルート登録は必ずこのメソッド経由で行うこと（Host 側で個別に <c>MapGet</c> 等を
/// 追加しない）。
/// </remarks>
public static class YaguraWebViewerExtensions
{
    /// <summary>
    /// 閲覧ページ（Razor Components）が要求する DI サービスを登録する。
    /// </summary>
    public static IServiceCollection AddYaguraWebViewer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 全画面 Interactive Server 単一モード（ADR-0003 決定 1）。方式を混在させない
        // ——静的 SSR との 2 方式を画面ごとに使い分ける規約は持たない、が同 ADR の
        // 採用理由の 1 つ（選択肢 (b) の却下理由）。
        // prerender は既定の有効のままとする（初回応答の HTML に描画結果が含まれ、
        // circuit 確立前でも内容が見える。E2E smoke はこの prerender 出力を検証する）。
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // MudBlazor（M8-1。ADR-0003 決定 3）。AddMudServices が登録するのは
        // ダイアログ・トースト・ポップオーバー等の DI サービスのみで、
        // エンドポイントは追加しない（ルート登録は下の MapYaguraWebViewer に集約されたまま）。
        services.AddMudServices();

        // 通知（トースト）の共通経路（M8-2。ui.md §3.1 通知規約）。ページは ISnackbar を
        // 直接使わず本経路を使う（低レベル API をページに散乱させない——ui.md §1）。
        // ISnackbar（AddMudServices が Scoped 登録）に合わせて Scoped。
        services.AddScoped<Components.Common.IYaguraNotifier, Components.Common.YaguraSnackbarNotifier>();

        // ---- circuit 統治（M8-4。Issue #71。security.md §2）----
        //
        // - CircuitRegistry: プロセス内の全 circuit の台帳（一覧・上限・回収の共通基盤）
        // - YaguraCircuitContext / YaguraCircuitHandler: circuit スコープの帰属・切断伝達。
        //   CircuitHandler は circuit ごとの DI スコープから解決されるため、Scoped 登録で
        //   circuit 1 本 = 1 インスタンスの対応になる
        // - IHttpContextAccessor: circuit 確立時の接続（WebSocket 要求）の実ローカルポートから
        //   リスナ帰属を判定するために使う（YaguraCircuitHandler のコメント参照——取得不能時は
        //   帰属不明 = 閲覧相当の安全側へ倒す）
        // - CircuitIdleReclaimService: 無操作 circuit の定期回収（SEC-8 仮値）
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CircuitRegistry>();
        services.AddScoped<YaguraCircuitContext>();
        services.AddScoped<CircuitHandler, YaguraCircuitHandler>();
        services.AddHostedService<CircuitIdleReclaimService>();

        // ---- circuit 認証状態の明示的な汲み直し（ADR-0010 決定 2・委任事項 2）----
        //
        // YaguraCircuitAuthenticationStateProvider は circuit スコープ（YaguraCircuitContext と
        // 同じ Scoped 登録）。AuthenticationStateProvider として登録することで
        // AddCascadingAuthenticationState()/<AuthorizeView> が同一インスタンスを消費し、
        // YaguraCircuitHandler.OnConnectionUpAsync が汲み直した最新の状態をそのまま反映する。
        services.AddScoped<YaguraCircuitAuthenticationStateProvider>();
        services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(
            sp => sp.GetRequiredService<YaguraCircuitAuthenticationStateProvider>());
        services.AddCascadingAuthenticationState();

        // ---- 送信元の逆引きホスト名（ADR-0007。ui.md §4）----
        //
        // - ReverseDnsDisplayOptions は Host が設定読み込み結果（Viewer:ReverseDns:Enabled）から
        //   構築して先に登録する。未登録のホスト（テストハーネス等）では TryAdd の既定 =
        //   「無効」に倒す——構成を持たない実行形態が外向き DNS クエリを発しないため（決定 4 の
        //   縮小側と同じ向き）
        // - 解決 API の呼び出しは IReverseDnsLookup の実装 1 点に集約する（オフ時・対象帯域外で
        //   下位 API へ到達しないことの単体テスト固定——security.md §1.1）
        services.TryAddSingleton(new ReverseDns.ReverseDnsDisplayOptions(Enabled: false));
        services.TryAddSingleton<ReverseDns.IReverseDnsLookup, ReverseDns.SystemDnsReverseLookup>();
        services.AddSingleton<Diagnostics.ReverseDnsMetrics>();
        services.AddSingleton<ReverseDns.IReverseDnsResolver, ReverseDns.ReverseDnsResolver>();

        // 閲覧 UI 認証（ADR-0010 Phase 4 決定 7）の実効値。既定は無効（現状維持——認証なし・LAN 公開）。
        // Host は解決済みの値を AddSingleton で後勝ちに上書きする（MainLayout の circuit 層 viewer ガード・
        // ViewerLoginScreen がこの型を DI 要求するため、閲覧認証を有効化しない構成でも解決できるよう既定を置く）。
        services.TryAddSingleton(Administration.ViewerAuthenticationRuntimeOptions.Disabled);

        return services;
    }

    /// <summary>
    /// 閲覧ページのルートを 1 箇所に集約してマップする。書き込み系エンドポイントは追加しない。
    /// </summary>
    /// <param name="endpoints">エンドポイントビルダー。</param>
    /// <param name="staticAssetsManifestPath">
    /// 静的アセットの endpoints マニフェストのパス（<c>AppContext.BaseDirectory</c> からの相対）。
    /// <c>null</c> の場合は既定解決（<c>{ApplicationName}.staticwebassets.endpoints.json</c>。
    /// Yagura.Host からの呼び出しはこちらで、build/publish の両出力に存在することを実機確認済み）。
    /// テストハーネスのようにエントリアセンブリがアプリ本体でないホストは、テスト出力に生成される
    /// RCL 単位のマニフェスト <c>Yagura.Web.staticwebassets.endpoints.json</c> を明示指定する
    /// （RCL 単位のマニフェストは publish 出力には含まれないことを実機確認済みのため、
    /// 本番経路の既定にはしない）。
    /// </param>
    /// <returns>
    /// Razor Components エンドポイントの規約ビルダー。<b>呼び出し側は必ずこれを
    /// <see cref="YaguraAdminExtensions.MapYaguraAdmin"/> へ渡すこと</b>——管理画面
    /// （<c>Yagura.Web.Administration.Screens</c> 配下の <c>@page</c> ルート）への
    /// Admin メタデータ付与（リスナ帰属の機械的導出）は MapYaguraAdmin が担う（M8-4）。
    /// </returns>
    /// <summary>
    /// 閲覧画面（<c>Yagura.Web.Components</c> 配下の <c>@page</c> ルート）の帰属判定に使う名前空間接頭辞。
    /// 閲覧認証（ADR-0010 Phase 4）有効時、この名前空間のページに <see cref="AdminAuthenticationExtensions.ViewerPolicyName"/> を
    /// 付与する（ログイン画面は除外）。管理画面（<c>Yagura.Web.Administration</c>）とは重ならない。
    /// </summary>
    private const string ViewerScreenNamespacePrefix = "Yagura.Web.Components";

    /// <summary>
    /// 閲覧認証を課さない閲覧ルート（ADR-0010 Phase 4）。未認証で到達できなければならないログイン画面自体に
    /// <see cref="AdminAuthenticationExtensions.ViewerPolicyName"/> を課すと循環（自己ロックアウト）になるため除外する。
    /// </summary>
    private static readonly HashSet<string> ViewerAuthExemptRouteTypeNames = new(StringComparer.Ordinal)
    {
        "Yagura.Web.Components.Pages.ViewerLoginScreen",
    };

    /// <param name="viewerAuthEnabled">
    /// 閲覧 UI 認証（<c>Viewer:Authentication:Windows:Enabled</c>。ADR-0010 Phase 4 決定 7）が有効か。
    /// <see langword="true"/> のとき、閲覧ページ（ログイン画面を除く）と CSV エクスポートに
    /// <see cref="AdminAuthenticationExtensions.ViewerPolicyName"/> を付与し、閲覧ログインエンドポイント
    /// （<c>/login/*</c>）を登録する。既定 <see langword="false"/>＝現状維持（認証なし）。
    /// </param>
    /// <param name="appAuthAvailable">
    /// 閲覧ログインでアプリ独自 ID/パスワードも受けるか（<c>Admin:Authentication:App:Enabled</c>。
    /// オーナー決定 2026-07-12）。<c>/login/app</c> の登録可否を制御する。
    /// </param>
    public static RazorComponentsEndpointConventionBuilder MapYaguraWebViewer(
        this IEndpointRouteBuilder endpoints,
        string? staticAssetsManifestPath = null,
        bool viewerAuthEnabled = false,
        bool appAuthAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // MudBlazor の同梱静的アセット（_content/MudBlazor/ 配下）の配信（M8-1）。
        // UseStaticFiles（ミドルウェア方式）ではなく MapStaticAssets（エンドポイント方式）を
        // 採る理由: 配信経路が EndpointDataSource の列挙に現れ、L-5 の全ルート許可リスト
        // 突合（ViewerEndpointAllowlistTests）の機械検証にそのまま乗るため（security.md §1 L-5）。
        // 静的アセットは GET/HEAD のみで、閲覧リスナの「書き込みエンドポイントを持たない」
        // 不変条件（ui.md §4）を破らない。
        endpoints.MapStaticAssets(staticAssetsManifestPath);

        // 外形監視・LB 用の liveness エンドポイント（Issue #126。2026-07-09 オーナー決定:
        // 採用。認証なし・内部情報を一切持たない固定レスポンス限定）。DB・カウンタ・バージョン等の
        // 内部状態には一切触れない純粋な生存確認——攻撃面の最小化（security.md §1 L-5 の許可リスト
        // 方針）と外形監視の要望のトレードオフを、公開して問題のない情報範囲（200 + 固定文字列のみ）
        // に絞ることで両立する。circuit を要しない静的応答であり、読み取り専用（GET/HEAD のみ）——
        // 閲覧リスナの「書き込みエンドポイントを持たない」不変条件を破らない。L-5 許可リストに登録済み。
        //
        // HEAD も受ける理由（PR #164 レビュー指摘）: 外形監視・LB ツールには帯域節約のため既定で
        // HEAD を送るものがあり、MapGet（GET のみ登録。HEAD へ自動フォールバックせず 405 になる）
        // では本エンドポイントの目的自体が一部のツール構成で達成できない。HTTP セマンティクス上
        // HEAD は「GET と同一ヘッダ・本文なし」であり、Kestrel が HEAD 応答の本文送出を抑止する
        // ため、ハンドラは GET と共通でよい（HealthEndpointTests で 200 + 本文なしを実機確認）。
        endpoints.MapMethods(
            "/health",
            new[] { HttpMethods.Get, HttpMethods.Head },
            () => Results.Text("OK", "text/plain; charset=utf-8"));

        // 接続終了の案内ページ（M8-4。security.md §2.2 の個別切断・SEC-8 の無操作回収の着地先）。
        // circuit を要しない静的応答であり、読み取り専用（GET のみ）——閲覧リスナの
        // 「書き込みエンドポイントを持たない」不変条件を破らない。L-5 許可リストに登録済み。
        endpoints.MapGet(CircuitGovernor.CircuitEndedPagePath, (HttpContext context) =>
        {
            var reason = context.Request.Query[CircuitGovernor.ReasonQueryParameter].ToString();
            var body = reason == CircuitTerminationReasons.IdleReclaimed
                ? UiText.CircuitEndedByIdleBody
                : UiText.CircuitEndedByAdministratorBody;

            var html =
                "<!DOCTYPE html><html lang=\"ja\"><head><meta charset=\"utf-8\" />" +
                $"<title>{UiText.CircuitEndedTitle}</title></head><body>" +
                $"<h1>{UiText.CircuitEndedTitle}</h1>" +
                $"<p>{body}</p>" +
                $"<p>{UiText.CircuitEndedReloadHint}</p>" +
                "</body></html>";

            return Results.Content(html, "text/html; charset=utf-8");
        });

        // ログ検索結果の CSV エクスポート（Issue #157）。読み取り専用の GET のみ——
        // 書き込みエンドポイントではない。L-5 許可リスト（ViewerEndpointAllowlistTests）に
        // 登録済み。閲覧認証有効時は閲覧画面と同じ認可判定（ViewerPolicy）を課す
        // （「画面で読めるものはエクスポートでも読める、以上の緩和をしない」——ADR-0010 決定 7）。
        MapLogSearchCsvExport(endpoints, viewerAuthEnabled);

        // 閲覧ログイン/ログアウトの HTTP エンドポイント（ADR-0010 Phase 4）。閲覧認証有効時のみ登録する
        // （無効なら閲覧はそもそも認証なしで到達でき、ログイン経路は不要——L-5 許可リストにも現れない）。
        if (viewerAuthEnabled)
        {
            endpoints.MapViewerAuthEndpoints(windowsAuthEnabled: viewerAuthEnabled, appAuthAvailable: appAuthAvailable);
        }

        // AddInteractiveServerRenderMode は Interactive Server の circuit エンドポイント
        // （SignalR。既定パス /_blazor）を有効化する。circuit 数の上限・origin 検証・無操作回収の
        // 統治は M8-4 で実装済み（CircuitGuardMiddleware・CircuitRegistry。security.md §2）。
        var razorComponents = endpoints.MapRazorComponents<YaguraWebApp>()
            .AddInteractiveServerRenderMode();

        // 閲覧認証有効時、閲覧ページ（Yagura.Web.Components 配下の @page。ログイン画面を除く）へ
        // ViewerPolicy を機械的に付与する（MapYaguraAdmin の管理側規約と対称。ADR-0010 Phase 4 決定 7）。
        // 管理画面（Yagura.Web.Administration）はこの名前空間の外にあり対象にならない。
        if (viewerAuthEnabled)
        {
            razorComponents.Add(endpointBuilder =>
            {
                var componentType = endpointBuilder.Metadata
                    .OfType<ComponentTypeMetadata>()
                    .FirstOrDefault()?.Type;

                if (componentType?.Namespace is string ns &&
                    (ns == ViewerScreenNamespacePrefix ||
                     ns.StartsWith(ViewerScreenNamespacePrefix + ".", StringComparison.Ordinal)) &&
                    !ViewerAuthExemptRouteTypeNames.Contains(componentType.FullName ?? string.Empty))
                {
                    endpointBuilder.Metadata.Add(new AuthorizeAttribute(AdminAuthenticationExtensions.ViewerPolicyName));
                }
            });
        }

        return razorComponents;
    }

    /// <summary>
    /// 検索結果 CSV エクスポートの上限件数（Issue #157）。ログ検索画面の表示上限
    /// （<c>LogSearch.SearchLimit</c>）と同じ 10,000 件——architecture.md §6 M-10 の仮値を
    /// そのまま踏襲し、画面に表示されている以上の件数を CSV へ出力しない（「件数上限の明示」を
    /// 独自の新しい仮値で増やさない判断。値の見直しは M-10 の確定と歩調を合わせる）。
    /// </summary>
    private const int CsvExportRecordLimit = 10_000;

    /// <summary>CSV エクスポートのクエリタイムアウト（検索画面の <c>SearchTimeout</c> と同値）。</summary>
    private static readonly TimeSpan CsvExportQueryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 一覧射影の 200 文字切り詰め（database.md §2.1・M-10 仮値）を回避し、CSV には全文を出力する
    /// ための <see cref="LogQuery.MessageProjectionLength"/> 上書き値。<c>MessageProjection.Truncate</c>
    /// は「メッセージ長がこの値以下ならそのまま返す」実装のため、実用上メッセージ全文を切り詰めない
    /// 上限として <see cref="int.MaxValue"/> を使う（database.md §1.2 の射影契約自体は変更しない——
    /// 呼び出し側オプションの利用のみ。LogQuery.cs の doc コメント参照）。
    /// </summary>
    private const int CsvExportMessageProjectionLength = int.MaxValue;

    /// <summary>
    /// 上限到達（打ち切り）を応答ヘッダーで明示するための名前（ui.md §5.3「検索の打ち切り」の
    /// CSV 版。CSV 本文へ非データ行を混入させない——監査提出用途で本文をデータのみに保つため、
    /// ヘッダーで明示する）。
    /// </summary>
    private const string CsvTruncatedHeaderName = "X-Yagura-Csv-Truncated";

    /// <summary>
    /// ログ検索結果の CSV エクスポート（Issue #157）。閲覧リスナの GET のみ——書き込みを
    /// 一切行わない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>検索条件との対応</b>: クエリパラメータ（<c>from</c>・<c>to</c>・<c>source</c>・
    /// <c>severity</c>・<c>facility</c>・<c>parseStatus</c>・<c>q</c>）は <c>LogSearch.razor</c> の
    /// 検索 URL（Issue #148 の URL 共有形式——<c>BuildQueryParameters</c>）と同一のキー・値形式で
    /// あり、<see cref="LogQuery"/> の対応フィールド（<c>severity</c> → <see cref="LogQuery.SeverityAtMost"/>
    /// の閾値・<c>facility</c> → 完全一致・<c>parseStatus</c> → enum 名・<c>q</c> → 自由文）へ
    /// 変換する。<b>不正な値は検索画面と同じく例外を出さず「条件なし」に安全側で丸める</b>
    /// （<c>LogSearch.razor</c> の <c>TryParseInt</c>／<c>TryParseParseStatus</c> と同じ寛容規則。
    /// エクスポートは検索画面から共有される URL の写しで呼ばれる想定であり、改変された URL で
    /// 400 を返すより検索画面と同じ解釈で応答を一致させる）。
    /// </para>
    /// <para>
    /// <b>CSV 形式</b>: RFC 4180 準拠のエスケープ・CSV インジェクション対策は
    /// <see cref="LogRecordCsvWriter"/>／<see cref="CsvField"/> が担う。<b>UTF-8 BOM</b> は本メソッドが
    /// <see cref="StreamWriter"/> に BOM 付与ありの <see cref="UTF8Encoding"/>
    /// （<c>encoderShouldEmitUTF8Identifier: true</c>）を渡すことで付与する（Excel の日本語文字化け
    /// 耐性——Issue #157 の受け入れ条件）。
    /// </para>
    /// <para>
    /// <b>メモリ節約</b>: CSV 全体を文字列へ組み立てず、<see cref="LogRecordCsvWriter.WriteAsync"/>
    /// がレコード 1 件ごとに応答ストリームへ直接書き出す。
    /// </para>
    /// </remarks>
    private static void MapLogSearchCsvExport(IEndpointRouteBuilder endpoints, bool viewerAuthEnabled)
    {
        var csvEndpoint = endpoints.MapGet("/search/export.csv", async (
            HttpContext context,
            string? from,
            string? to,
            string? source,
            string? severity,
            string? facility,
            string? parseStatus,
            string? q,
            ILogStore logStore) =>
        {
            var query = new LogQuery(
                Limit: CsvExportRecordLimit,
                Timeout: CsvExportQueryTimeout,
                ReceivedAtFrom: TryParseUtcTimestamp(from),
                ReceivedAtTo: TryParseUtcTimestamp(to),
                SourceAddress: string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
                SeverityAtMost: TryParseInt(severity),
                Facility: TryParseInt(facility),
                ParseStatus: TryParseParseStatus(parseStatus),
                SearchText: string.IsNullOrWhiteSpace(q) ? null : q,
                MessageProjectionLength: CsvExportMessageProjectionLength);

            var results = await logStore.QueryAsync(query, context.RequestAborted).ConfigureAwait(false);

            context.Response.ContentType = "text/csv; charset=utf-8";
            context.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{BuildCsvFileName()}\"";

            if (results.Count >= CsvExportRecordLimit)
            {
                context.Response.Headers[CsvTruncatedHeaderName] = "true";
            }

            // BOM 付与ありの UTF8Encoding: StreamWriter は先頭書き込み時に GetPreamble() を
            // 自動出力する（.NET の StreamWriter の既定動作。Encoding.UTF8 静的プロパティも同じ
            // 既定だが、意図を読み手に明示するため生成引数を明示する）。leaveOpen: true——
            // context.Response.Body の完了・破棄は ASP.NET Core の応答パイプライン自体の責務であり、
            // 本メソッドが Stream.Dispose を通じて先取りしない（バッファのフラッシュのみ行う）。
            await using var writer = new StreamWriter(
                context.Response.Body,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                bufferSize: 1024,
                leaveOpen: true);
            await LogRecordCsvWriter.WriteAsync(writer, results, context.RequestAborted).ConfigureAwait(false);
            await writer.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        });

        if (viewerAuthEnabled)
        {
            // 閲覧画面と同じ認可（ViewerPolicy）を課す（ADR-0010 決定 7——エクスポート緩和をしない）。
            csvEndpoint.RequireAuthorization(AdminAuthenticationExtensions.ViewerPolicyName);
        }
    }

    /// <summary>CSV ダウンロードのファイル名（生成時刻を UTC で埋め込み、上書き事故を避ける）。</summary>
    private static string BuildCsvFileName() =>
        $"yagura-log-search-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv";

    // ---- クエリ値の寛容な解釈（LogSearch.razor の TryParseInt / TryParseParseStatus /
    //      ParseServerWallClock と同じ規則: 不正な値は例外を出さず「条件なし」= null に丸める）----

    /// <summary>期間クエリ（往復形式 "O"。LogSearch の FormatQueryTimestamp と対）の寛容な解釈。</summary>
    private static DateTimeOffset? TryParseUtcTimestamp(string? value) =>
        value is { Length: > 0 } &&
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    private static int? TryParseInt(string? value) =>
        value is { Length: > 0 } &&
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static ParseStatus? TryParseParseStatus(string? value) =>
        value is { Length: > 0 } && Enum.TryParse<ParseStatus>(value, out var parsed) ? parsed : null;
}
