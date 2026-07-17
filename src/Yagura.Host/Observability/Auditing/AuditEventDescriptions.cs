using Yagura.Abstractions.Auditing;

namespace Yagura.Host.Observability.Auditing;

/// <summary>
/// <see cref="AuditEventKind"/> から Windows イベントログ本文向けの日本語説明への対応表
/// （2026-07-06 イベントログ日本語化。security.md §4.3 の意味とレベルの凍結は ID・レベルに
/// 対する規約であり、本文の文言自体は additive-only の対象ではないため、既存 ID の説明文を
/// 日本語化しても互換性を壊さない）。
/// </summary>
/// <remarks>
/// <para>
/// <b>UiText との違い</b>: <c>Yagura.Web.Components.Common.UiText</c> はブラウザ表示（Blazor
/// コンポーネント）向けの文言集約であり、本クラスは Windows イベントログ本文（Host が
/// <see cref="Microsoft.Extensions.Logging.ILogger"/> 経由で書く運用系メッセージ）向けである。
/// 参照方向・ライフサイクルが異なるモジュール（<c>Yagura.Web</c> と <c>Yagura.Host</c>）に
/// それぞれ属するため、意図的に分離する。
/// </para>
/// <para>
/// <b>アプリ記録ファイル（<see cref="AuditFileLine.Kind"/>）とは別系統</b>: JSON Lines の
/// <c>Kind</c> フィールドは <see cref="AuditEventKind"/> の英語 enum 名（
/// <c>Enum.ToString()</c>）のまま維持する——外部ツール（grep・jq 等）による機械的な
/// 事後解析対象であり、日本語化すると既存の解析スクリプトの文字列一致を壊すおそれがある
/// （FileAuditRecorder.TryAppendToFile 参照）。日本語化の対象は、あくまで人間が読む
/// Windows イベントログの本文（<see cref="FileAuditRecorder.TryWriteEventLog"/> 経由）に限る。
/// </para>
/// </remarks>
internal static class AuditEventDescriptions
{
    /// <summary>
    /// 事象種別に対応する日本語の短い説明を返す（イベントログ本文の {Kind} 相当部分に使う）。
    /// </summary>
    public static string Describe(AuditEventKind kind) => kind switch
    {
        AuditEventKind.ViewerListenerAdminRequestRejected => "閲覧リスナへの管理操作を拒否",
        AuditEventKind.ConfigurationSaved => "設定変更を適用",
        AuditEventKind.PromotionConnectionValidated => "本番昇格の接続検証を実施",
        AuditEventKind.PromotionExecuted => "本番昇格を実行",
        AuditEventKind.CircuitDisconnected => "circuit を切断",
        AuditEventKind.CircuitOriginRejected => "circuit 確立要求の origin 検証で拒否",
        AuditEventKind.ForwarderKitGenerated => "フォワーダ配布キットを生成",
        AuditEventKind.AdminAuthenticationConfigured => "管理 UI 認証設定を変更",
        AuditEventKind.AdminAccountCreated => "管理者アカウントを作成/変更",
        AuditEventKind.WindowsAuthenticationHandshakeFailed => "Windows 統合認証のハンドシェイクに失敗",
        AuditEventKind.AppAuthenticationLoginFailed => "アプリ独自認証のログインに失敗",
        AuditEventKind.AdminAccountLockedOut => "管理者アカウントをロックアウト",
        AuditEventKind.AdminLoginSucceeded => "管理 UI へサインイン",
        AuditEventKind.AdminAuthorizationDenied => "認証成功後に管理者権限がなくアクセスを拒否",
        AuditEventKind.AdminHttpsCertificatePrivateKeyAccessGranted => "管理 UI HTTPS 証明書の秘密鍵アクセス権を付与",
        AuditEventKind.IngestionTlsCertificatePrivateKeyAccessGranted => "TLS 受信証明書の秘密鍵アクセス権を付与",
        AuditEventKind.AdminAuthBackoffCapReached => "アプリ独自認証のバックオフが上限に到達",
        AuditEventKind.AdminAuthRateLimited => "アプリ独自認証のログイン試行をレート制限で拒否",
        AuditEventKind.AdminRemoteBindingConfigured => "管理リスナのリモートバインド設定を変更",
        AuditEventKind.AdminHttpsCertificateConfigured => "管理 UI リモート HTTPS の証明書設定を変更",
        AuditEventKind.AdminSessionsInvalidated => "認証セッションを緊急全失効（世代番号バンプ）",
        AuditEventKind.ViewerLoginSucceeded => "閲覧 UI へサインイン",
        AuditEventKind.ViewerAuthorizationDenied => "Windows 認証成功後に閲覧/管理グループ非所属でアクセスを拒否",
        AuditEventKind.AuditRetentionApplied => "保持期間を超過した監査記録ファイルを削除",
        AuditEventKind.ConfigurationReloaded => "設定を再読み込み",
        AuditEventKind.InstallationRecordTranscribed => "インストール記録を転記",
        AuditEventKind.LogMigrationExecuted => "蓄積ログを移行",
        AuditEventKind.CircuitRevocationGraceGranted => "失効後の閲覧継続を猶予として許容",
        AuditEventKind.CircuitRevocationGraceEnded => "猶予中の circuit が終了",
        AuditEventKind.RejectionAggregated => "拒否試行を集約記録",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知の監査事象種別。"),
    };
}
