using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Web.Circuits;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web;

/// <summary>
/// Yagura.Web（管理リスナ）を Host から組み込むための拡張メソッド（M6-1。Issue #51 →
/// M8-4。Issue #71 で管理画面の実体を実装）。
/// </summary>
/// <remarks>
/// <b>管理系ルートの帰属宣言は本クラスに集約する</b>（<see cref="YaguraWebViewerExtensions.MapYaguraWebViewer"/>
/// と対の集約点）。管理画面（設定ウィザード・本番昇格・circuit 管理。ui.md §4 の
/// 「設定（ウィザード群）」画面）は Razor Components のページ
/// （<c>Yagura.Web.Administration.Screens</c> 名前空間）として実装され、そのルート自体は
/// <c>MapRazorComponents</c>（<see cref="YaguraWebViewerExtensions.MapYaguraWebViewer"/> 内）が
/// 登録する——本クラスの <see cref="MapYaguraAdmin"/> は、それらのエンドポイントへ
/// <see cref="ListenerPortGuardEndpointMetadata.Admin"/> を機械的に付与する規約（convention）を
/// 差し込むことで「管理系ルート = 管理リスナ帰属」を成立させる（L-3b の「ルート表からの
/// 機械的導出」と同じ思想——画面の配置（名前空間）がそのまま帰属の真実源になる）。
/// </remarks>
public static class YaguraAdminExtensions
{
    /// <summary>
    /// 管理画面（Razor Components ページ）の帰属判定に使う名前空間接頭辞。
    /// この名前空間に置かれたページは自動的に管理リスナ帰属（Admin メタデータ付与 =
    /// <c>ListenerPortGuardMiddleware</c> のガード対象）になる。
    /// </summary>
    /// <remarks>
    /// 閲覧側コンポーネントの名前空間（<c>Yagura.Web.Components</c> 配下——
    /// <c>ViewerComponentReferenceIsolationTests</c> の検査対象）と重ならないこと。
    /// 管理画面が書き込み系サービス（<see cref="IYaguraWriteService"/>）を注入するのは正当であり、
    /// 名前空間の分離が「閲覧側の参照分離検査」と「管理側の帰属導出」の両方の境界線になる。
    /// </remarks>
    public const string AdminScreenNamespacePrefix = "Yagura.Web.Administration";

    /// <summary>
    /// 管理系の書き込みサービス（circuit 管理）を DI へ登録する（M8-4）。
    /// ウィザード系サービス（<c>ISetupWizardService</c> / <c>IPromotionWizardService</c>）の実体は
    /// 設定ファイル・DB 接続を管轄する <c>Yagura.Host</c> 側が結線する（architecture.md §1.1 の
    /// 参照構造。circuit 管理だけは circuit 台帳が Web 層にあるためここで登録する）。
    /// </summary>
    public static IServiceCollection AddYaguraAdmin(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICircuitManagementService, Administration.CircuitManagementService>();
        services.TryAddSingleton<INicCandidateSource, SystemNicCandidateSource>();

        return services;
    }

    /// <summary>
    /// 管理リスナ専用ルートの帰属宣言を 1 箇所に集約して適用する。
    /// </summary>
    /// <param name="endpoints">エンドポイントビルダー。</param>
    /// <param name="razorComponents">
    /// <see cref="YaguraWebViewerExtensions.MapYaguraWebViewer"/> の戻り値（Razor Components
    /// エンドポイントの規約ビルダー）。管理画面のページエンドポイントへの Admin メタデータ付与に
    /// 使う。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>付与の仕組み</b>: Razor Components の各ページエンドポイントは
    /// <see cref="ComponentTypeMetadata"/>（ページコンポーネントの型）をメタデータに持つ
    /// （.NET 10.0.9 の公開型として実機確認済み。2026-07-06）。その型の名前空間が
    /// <see cref="AdminScreenNamespacePrefix"/> 配下なら <see cref="ListenerPortGuardEndpointMetadata.Admin"/>
    /// を追加する——以降は M6-1 以来の <see cref="ListenerPortGuardMiddleware"/> がそのまま
    /// 効き、閲覧リスナ（8514）経由の直接到達は 404 + 監査記録（3001）になる。
    /// </para>
    /// <para>
    /// <b>確立済み circuit 上の対話的ナビゲーションはこのガードに現れない</b>（security.md §1
    /// L-5 の覆域の限界）ため、管理画面は共通レイアウト
    /// （<c>Yagura.Web.Administration.Screens.AdminScreenLayout</c>）で circuit 層の帰属検査を
    /// 併用する（二層防御。AdminScreenAccessPolicy 参照）。
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapYaguraAdmin(
        this IEndpointRouteBuilder endpoints,
        RazorComponentsEndpointConventionBuilder razorComponents)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(razorComponents);

        razorComponents.Add(endpointBuilder =>
        {
            var componentType = endpointBuilder.Metadata
                .OfType<ComponentTypeMetadata>()
                .FirstOrDefault()?.Type;

            if (componentType?.Namespace is string ns &&
                (ns == AdminScreenNamespacePrefix ||
                 ns.StartsWith(AdminScreenNamespacePrefix + ".", StringComparison.Ordinal)))
            {
                endpointBuilder.Metadata.Add(ListenerPortGuardEndpointMetadata.Admin);
            }
        });

        MapForwarderKitDownload(endpoints);

        return endpoints;
    }

    /// <summary>
    /// フォワーダ配布キットのダウンロードエンドポイント（ADR-0008 設計条件 7・9・委任 #1〜#3・#5・#7）。
    /// Razor Components のページではなく素の <c>MapGet</c> であるため、
    /// <see cref="ListenerPortGuardEndpointMetadata.Admin"/> は名前空間規約
    /// （<see cref="AdminScreenNamespacePrefix"/> 由来の自動付与）の対象外——ここで明示的に
    /// 付与する（管理系ルートの帰属宣言を本クラスに集約するという remarks の方針どおり）。
    /// </summary>
    /// <remarks>
    /// 生成は ZIP をメモリ上で組み立てるのみで、外部ネットワークへアクセスせず、ディスクへ
    /// 一時ファイルも書かない（<see cref="ForwarderKitBuilder"/> の remarks 参照。MSI 同梱時は
    /// 配置済みファイルを読み取って封入するのみ）。検証失敗は 400、成功時は
    /// <c>application/zip</c> で応答する。生成操作は監査記録（2000 番台 ID 2005。
    /// ADR-0008 設計条件 6・9）の対象とする。<c>includeMsi=true</c> のとき、配置フォルダが
    /// 単一検出でなければ 400（画面の二段階確認を通っていない版不一致は
    /// <see cref="ForwarderKitRequest.TryCreate(string?, int, string?, ForwarderMsiBundle?, out ForwarderKitRequest?, out ForwarderKitValidationError?)"/>
    /// 側の最終防御で 400 になる）。
    /// </remarks>
    private static void MapForwarderKitDownload(IEndpointRouteBuilder endpoints)
    {
        var endpoint = endpoints.MapGet("/admin/forwarder-kit/download", async (
            HttpContext context,
            string? host,
            int? port,
            string? channels,
            bool? includeMsi,
            bool? msiVersionMismatchAcknowledged,
            IAuditRecorder auditRecorder,
            TimeProvider timeProvider,
            IForwarderMsiSource msiSource) =>
        {
            ForwarderMsiBundle? msiBundle = null;

            if (includeMsi == true)
            {
                var lookup = msiSource.Lookup();
                if (lookup.State != ForwarderMsiLookupState.Single)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync(FormatMsiLookupError(lookup.State)).ConfigureAwait(false);
                    return;
                }

                var details = lookup.Details!;
                var effectiveVersion = ForwarderMsiFilter.ResolveEffectiveVersion(details.ProductVersion, details.FileName);
                var versionMismatch = !ForwarderMsiFilter.MatchesVerifiedVersion(effectiveVersion, ForwarderKitConstraints.VerifiedFluentBitVersion);
                var officialHashMatch = ForwarderMsiFilter.MatchesOfficialHash(details.Sha256, ForwarderMsiConstraints.OfficialSha256ForVerifiedVersion);

                msiBundle = new ForwarderMsiBundle(
                    details.FilePath,
                    details.FileName,
                    effectiveVersion,
                    details.Sha256,
                    officialHashMatch,
                    versionMismatch,
                    msiVersionMismatchAcknowledged == true);
            }

            if (!ForwarderKitRequest.TryCreate(
                    host,
                    port ?? ForwarderKitConstraints.DefaultPort,
                    channels,
                    msiBundle,
                    out var request,
                    out var error))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync(FormatValidationError(error!.Value)).ConfigureAwait(false);
                return;
            }

            var generatedAt = timeProvider.GetLocalNow();
            var zipBytes = ForwarderKitBuilder.Build(request!, generatedAt);

            // CancellationToken.None: クライアント切断（RequestAborted）で監査記録自体を
            // 打ち切らない——生成した事実は応答の成否に関わらず記録する
            // （ListenerPortGuardMiddleware と同じ判断。ADR-0004 決定 7）。
            await auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: timeProvider.GetUtcNow(),
                    Kind: AuditEventKind.ForwarderKitGenerated,
                    RemoteAddress: context.Connection.RemoteIpAddress?.ToString(),
                    RemotePort: context.Connection.RemotePort,
                    Detail: FormatAuditDetail(request!)),
                CancellationToken.None).ConfigureAwait(false);

            var fileName = ForwarderKitBuilder.BuildFileName(request!, generatedAt);
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            context.Response.ContentType = "application/zip";
            await context.Response.Body.WriteAsync(zipBytes).ConfigureAwait(false);
        });

        endpoint.WithMetadata(ListenerPortGuardEndpointMetadata.Admin);
    }

    /// <summary>検証エラーの応答文言（管理者向け・機械可読なキーワードを含む簡潔な英語文）。</summary>
    private static string FormatValidationError(ForwarderKitValidationError error) => error switch
    {
        ForwarderKitValidationError.HostRequired => "host is required.",
        ForwarderKitValidationError.HostInvalid => "host contains characters outside the allowed set.",
        ForwarderKitValidationError.PortOutOfRange => "port must be between 1 and 65535.",
        ForwarderKitValidationError.ChannelsInvalid => "channels must be a comma-separated subset of System, Application, Security.",
        ForwarderKitValidationError.MsiVersionMismatchNotAcknowledged =>
            "msi version differs from the verified version and was not acknowledged.",
        _ => "invalid request.",
    };

    /// <summary>MSI 配置フォルダの検出状態が単一でない場合の応答文言（ADR-0008 設計条件 9）。</summary>
    private static string FormatMsiLookupError(ForwarderMsiLookupState state) => state switch
    {
        ForwarderMsiLookupState.NotFound => "includeMsi=true was requested, but no MSI was found in the placement folder.",
        ForwarderMsiLookupState.Multiple => "includeMsi=true was requested, but multiple MSIs were found in the placement folder.",
        _ => "invalid msi state.",
    };

    /// <summary>
    /// 監査 Detail の構造化文字列（ADR-0008 設計条件 6・9・委任 #5）。既存の host/port/channels に
    /// 加え、MSI 同梱時は msiVersion・msiSha256・officialHashMatch・versionMismatchAcknowledged を
    /// 記録する（秘密情報は含めない——値は来歴情報のみ）。
    /// </summary>
    private static string FormatAuditDetail(ForwarderKitRequest request)
    {
        var detail = $"host={request.Host} port={request.Port} channels={request.ChannelsValue} msiBundled={(request.IncludeMsi ? "true" : "false")}";

        if (request.MsiBundle is { } bundle)
        {
            var officialMatchValue = bundle.OfficialHashMatch switch
            {
                OfficialHashMatchResult.Match => "yes",
                OfficialHashMatchResult.Mismatch => "no",
                _ => "unverified",
            };

            detail +=
                $" msiVersion={bundle.ProductVersion ?? "unknown"}" +
                $" msiSha256={bundle.Sha256}" +
                $" officialHashMatch={officialMatchValue}" +
                $" versionMismatchAcknowledged={(bundle.VersionMismatchAcknowledged ? "true" : "false")}";
        }

        return detail;
    }

    /// <summary>
    /// <see cref="ListenerPortGuardMiddleware"/> をパイプラインに組み込む。
    /// </summary>
    /// <param name="app">アプリケーションビルダー。</param>
    /// <param name="adminPort">管理リスナの実ポート番号（<c>ResolvedYaguraConfiguration.AdminHttpPort</c>）。</param>
    /// <remarks>
    /// <para>
    /// <c>UseRouting</c>（または暗黙にルーティングを組み込む <c>MapRazorComponents</c> 等）の
    /// 後、エンドポイントの実処理が始まる前に呼び出すこと——<c>HttpContext.GetEndpoint()</c>
    /// がルーティング確定後のメタデータを返すために必要な順序。Program.cs 参照。
    /// </para>
    /// <para>
    /// <b>DI 前提（M6-2）</b>: <see cref="ListenerPortGuardMiddleware"/> は明示引数
    /// <c>adminPort</c> 以外に <c>Yagura.Abstractions.Auditing.IAuditRecorder</c> と
    /// <c>Yagura.Web.Diagnostics.WebGuardMetrics</c> をコンストラクタで要求する。
    /// <c>UseMiddleware</c> は明示引数以外のコンストラクタパラメータを DI コンテナから
    /// 解決するため、呼び出し前に両方のサービスを <c>IServiceCollection</c> へ登録しておくこと
    /// （<c>IAuditRecorder</c> の実体は <c>Yagura.Host</c> 側が結線する。
    /// <c>WebGuardMetrics</c> は本クラスと対称のシングルトンとして Program.cs が登録する）。
    /// </para>
    /// </remarks>
    public static IApplicationBuilder UseYaguraListenerPortGuard(this IApplicationBuilder app, int adminPort)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<ListenerPortGuardMiddleware>(adminPort);
    }

    /// <summary>
    /// circuit 統治のガード（origin 検証 + circuit 数上限。<see cref="CircuitGuardMiddleware"/>）を
    /// パイプラインに組み込む（M8-4。security.md §2.1・§2.2）。
    /// </summary>
    /// <remarks>
    /// <see cref="UseYaguraListenerPortGuard"/> の直後（= ルーティング確定後・エンドポイント
    /// 実行前）に呼び出すこと。管理系 404（存在を漏らさない応答）が上限案内より先に判定される
    /// 順序を保つ。DI 前提: <c>CircuitRegistry</c>・<c>YaguraAdminListenerPort</c>・
    /// <c>IAuditRecorder</c>・<c>WebGuardMetrics</c>。
    /// </remarks>
    public static IApplicationBuilder UseYaguraCircuitGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<CircuitGuardMiddleware>();
    }
}
