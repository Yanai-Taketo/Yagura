namespace Yagura.Web;

/// <summary>
/// エンドポイントが「どのリスナ（ポート）に帰属するか」を宣言するメタデータ（M6-1。Issue #51）。
/// </summary>
/// <remarks>
/// <para>
/// <b>実行時の強制は <see cref="ListenerPortGuardMiddleware"/> が担う</b>。本メタデータ自体は
/// 「宣言」に過ぎず、強制はミドルウェアがリクエストパイプラインの早い段階（ルーティング確定の
/// 直後・エンドポイント実行の前）で行う。管理系エンドポイント（<see cref="YaguraAdminExtensions.MapYaguraAdmin"/>
/// が登録するもの）には <see cref="Admin"/> を付与し、閲覧リスナ（LAN 公開ポート）経由の接続では
/// 到達できないようにする——閲覧リスナに到達した管理系要求は 404 になる（security.md §1 L-3b の
/// 「実行されない」構造。「拒否 + 監査記録」自体は後続 Issue #52 のスコープ）。
/// </para>
/// <para>
/// <b>RequireHost を採らなかった理由</b>: ASP.NET Core の <c>RequireHost</c> は HTTP
/// <c>Host</c> ヘッダで判定するため、クライアントが任意の <c>Host</c> ヘッダ（例:
/// <c>Host: anything:8515</c>）を送れば閲覧リスナ（非 loopback）経由でも管理系ルートの
/// 判定条件を満たしてしまう——ヘッダは接続元を偽装できるため、防御として信頼できない
/// （ASP.NET Core 公式ドキュメントも「<c>RequireHost</c> はクライアントによる偽装の
/// 対象になり得る」と明記している）。本メタデータは <see cref="ListenerPortGuardMiddleware"/>
/// が参照する <c>HttpContext.Connection.LocalPort</c>（実際に接続を受け付けた TCP ソケットの
/// ローカルポート。Kestrel のトランスポート層が設定する値でありクライアントからは偽装不能）
/// と組み合わせて初めて安全な判定になる。
/// </para>
/// </remarks>
public sealed class ListenerPortGuardEndpointMetadata
{
    private ListenerPortGuardEndpointMetadata(ListenerKind kind)
    {
        Kind = kind;
    }

    /// <summary>管理リスナ専用エンドポイントに付与するメタデータ。</summary>
    public static ListenerPortGuardEndpointMetadata Admin { get; } = new(ListenerKind.Admin);

    /// <summary>このエンドポイントが帰属するリスナの種別。</summary>
    public ListenerKind Kind { get; }
}

/// <summary>リスナの種別（M6-1）。</summary>
public enum ListenerKind
{
    /// <summary>閲覧リスナ（LAN 公開が既定。configuration.md §4.2）。</summary>
    Viewer,

    /// <summary>管理リスナ（loopback 限定。configuration.md §4.2・security.md §1 L-4）。</summary>
    Admin,
}
