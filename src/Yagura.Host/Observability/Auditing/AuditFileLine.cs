namespace Yagura.Host.Observability.Auditing;

/// <summary>
/// アプリ記録ファイル（監査記録。security.md §4.2）の JSON Lines 1 行分の形式。
/// </summary>
/// <remarks>
/// <see cref="Yagura.Abstractions.Auditing.AuditEvent"/> をそのままシリアライズしない理由:
/// 本形式はファイル上の永続表現として独立に安定させたい（<c>AuditEvent</c> 側のプロパティ名・
/// 型変更が、既に書き出し済みのファイルの読み直し互換性に直結することを避ける。
/// <c>Yagura.Host.Configuration.YaguraConfigurationWriter</c>・<c>MetadataStore</c> の
/// 「ファイル形式は独自の FileFormat 型を介する」という既存パターンと同じ判断）。
/// </remarks>
internal sealed class AuditFileLine
{
    /// <summary>形式バージョン（将来の破壊的変更検出用）。</summary>
    public int Version { get; init; } = 1;

    /// <summary>事象発生時刻（UTC。ISO 8601 ラウンドトリップ形式）。</summary>
    public string? OccurredAtUtc { get; init; }

    /// <summary>事象種別（<see cref="Yagura.Abstractions.Auditing.AuditEventKind"/> の文字列表現）。</summary>
    public string? Kind { get; init; }

    /// <summary>Windows イベントログの併記先イベント ID（<see cref="AuditEventIds"/>）。</summary>
    public int EventId { get; init; }

    /// <summary>接続元アドレス。</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>接続元ポート。</summary>
    public int? RemotePort { get; init; }

    /// <summary>試行されたパス（拒否系事象のみ。管理操作では null）。</summary>
    public string? AttemptedPath { get; init; }

    /// <summary>到達したリスナの実ポート番号（拒否系事象のみ。管理操作では null。M8-4 で nullable 化——additive）。</summary>
    public int? ReachedListenerPort { get; init; }

    /// <summary>事象の要約（変更キー・成否等。秘密情報は含まない——security.md §4.1。M8-4 で追加——additive）。</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// 認証方式（<c>"windows"</c> / <c>"app"</c>。ADR-0010 決定 3・6 で追加——additive。
    /// 認証を経由しない操作では null。
    /// </summary>
    public string? AuthenticationScheme { get; init; }

    /// <summary>
    /// 認証済み利用者名（ADR-0010 決定 3・6 で追加——additive。<see cref="AuthenticationScheme"/> と
    /// 組み合わせて命名空間つきの「誰が」表記になる）。
    /// </summary>
    public string? AuthenticatedPrincipal { get; init; }
}
