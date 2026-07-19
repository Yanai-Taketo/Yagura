namespace Yagura.Abstractions.Administration;

/// <summary>
/// メール通知（ADR-0017。opt-in・既定無効）の設定・テスト送信・健全性参照
/// （管理画面 <c>/admin/email-notification</c> の書き込み系サービス）。
/// </summary>
/// <remarks>
/// <para>
/// <b>拒否と警告の区別</b>（実装がこの区別を潰さないための宣言）:
/// </para>
/// <list type="table">
/// <item>
///   <term>拒否（<see cref="WizardValidationException"/>）</term>
///   <description>
///   有効化しようとしているのに差出人・宛先・SMTP ホストのいずれかが空、アドレスの形式が
///   不正、ポートが 1〜65535 の外、SMTP 認証のユーザー名とパスワードの片方だけが指定されている
///   ——いずれも<b>保存しない</b>。設定ファイルへ書けば <c>YaguraConfigurationLoader</c> が
///   機能を無効化して警告する状態になるため、保存前に画面で止めるほうが原因に近い。
///   </description>
/// </item>
/// <item>
///   <term>警告して受理</term>
///   <description>
///   パスワードを設定しているのに <c>Security</c> が <c>required</c> でない構成
///   （STARTTLS ストリップで漏れるのは通知内容ではなく SMTP 資格情報である旨を伝える。
///   ADR-0017 決定 3）。運用上の選択であり得るため保存はする。
///   </description>
/// </item>
/// </list>
/// </remarks>
public interface IEmailNotificationAdminService : IYaguraWriteService
{
    /// <summary>現在の設定とチャネル健全性を取得する。</summary>
    Task<EmailNotificationStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 設定を保存する（監査 2021）。
    /// </summary>
    /// <param name="settings">画面で編集された設定。</param>
    /// <exception cref="WizardValidationException">上表の「拒否」に該当する場合。</exception>
    Task<EmailNotificationConfigureResult> ConfigureAsync(
        EmailNotificationSettings settings,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// テスト送信を行う（監査 2022）。
    /// </summary>
    /// <remarks>
    /// <b>画面に入力中の（未保存の）値で送信する</b>——保存前の検証こそがテスト送信の価値であり、
    /// 誤設定を保存しないと試せない作りにしない（決定 8）。キュー・流量上限を経由しない直送
    /// （テストが実通知の枠を消費しない）。
    /// <para>
    /// <b>パスワード欄が空欄のままのテスト送信は、保存済みのパスワードを使用する</b>
    /// （パスワードは UI に再表示しないため、空欄 = 「変更しない」の意味に固定する）。
    /// この形態は「画面上の任意ホストへ保存済み資格情報で AUTH を試行できる」操作になるため、
    /// 監査レコードに「保存済み資格情報を使用」の別を含める（値は含めない）。
    /// </para>
    /// </remarks>
    Task<EmailNotificationTestResult> SendTestAsync(
        EmailNotificationSettings settings,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 画面で編集されるメール通知設定。
/// </summary>
/// <param name="Password">
/// <see langword="null"/> または空文字は「変更しない」（保存済みの値を維持する）。
/// パスワードは UI に再表示しないため、空欄をこの意味に固定する（決定 8）。
/// </param>
/// <param name="ClearPassword">
/// 保存済みのパスワードを削除する（明示操作）。「空欄 = 変更しない」に固定した帰結として、
/// 削除には専用の口が要る——これがないと、一度パスワードを保存した構成は SMTP 認証を
/// やめられなくなる（ユーザー名を消すと決定 3 の「両方あり/両方なし」検証に必ず落ちる。
/// PR #366 レビュー対応）。<paramref name="Password"/> との同時指定は拒否される。
/// テスト送信では「保存済みパスワードへのフォールバックをしない」の意味になる。
/// </param>
public sealed record EmailNotificationSettings(
    bool Enabled,
    string? From,
    IReadOnlyList<string> To,
    string? SmtpHost,
    int SmtpPort,
    string Security,
    string? Username,
    string? Password,
    bool ClearPassword = false);

/// <summary>
/// チャネルの健全性（管理面ダッシュボードの常設カード。ADR-0017 決定 5）。
/// </summary>
/// <remarks>
/// <b>このカードが「イベントログを見ない現場」の前提と能動通知の循環を閉じる</b>——
/// メール通知の一次到達先はイベントログだが、本 ADR 自身が「イベントログを見ない現場」を
/// 前提にしている。チャネルの静かな死（パスワード失効・リレー廃止等）に日常動線で気づける
/// 経路を確保することで、at-most-once + 破棄という設計が初めて正当化される。
/// </remarks>
/// <param name="SuppressedCountByEventId">
/// 抑制された EventId ごとの回数。<b>累積回数だけにしない</b>——回数のみでは「先週の 1 回の
/// 障害でついた数字」と「毎晩じわじわ増えている」が区別できず、何が届かなかったかも
/// 特定できない。
/// </param>
/// <param name="DisabledByInvalidConfiguration">
/// 設定不正により機能が無効化されている（決定 2 の縮退）。<see cref="Enabled"/> が
/// <see langword="true"/> なのにこれも <see langword="true"/> なら「有効にしたつもりで
/// 送られていない」状態であり、画面上で最も目立たせるべき状態である。
/// </param>
public sealed record EmailNotificationChannelHealth(
    DateTimeOffset? LastSuccessAt,
    string? LastFailureKind,
    string? LastFailureDetail,
    int QueueDepth,
    int DroppedCount,
    int SuppressedCount,
    DateTimeOffset? LastSuppressedAt,
    IReadOnlyDictionary<int, int> SuppressedCountByEventId,
    bool DisabledByInvalidConfiguration);

/// <summary>現在の設定とチャネル健全性。</summary>
/// <param name="PasswordConfigured">
/// パスワードが保存済みか（値は返さない——UI に再表示しない。決定 3）。
/// </param>
public sealed record EmailNotificationStatus(
    bool Enabled,
    string? From,
    IReadOnlyList<string> To,
    string? SmtpHost,
    int SmtpPort,
    string Security,
    string? Username,
    bool PasswordConfigured,
    EmailNotificationChannelHealth Health);

/// <summary>設定保存の結果。</summary>
/// <param name="PlaintextCredentialWarning">
/// パスワードを設定しているのに <c>Security</c> が <c>required</c> でない（決定 3 の能動警告）。
/// </param>
public sealed record EmailNotificationConfigureResult(
    IReadOnlyList<string> ChangedKeys,
    ConfigurationApplyEffect RequiredEffect,
    bool PlaintextCredentialWarning,
    EmailNotificationStatus Status);

/// <summary>
/// テスト送信の結果（ADR-0017 決定 8・委任 12）。
/// </summary>
/// <param name="Guidance">
/// 失敗時の<b>平易な日本語の説明と次の一手</b>。生の SMTP 応答だけを見せると、原因に辿り着けない
/// 利用者がパスワードを打ち直してアカウントをロックする——決定 3 が避けたい事態そのものになる。
/// </param>
/// <param name="ServerResponse">
/// 参考情報としてのサーバ応答（<see cref="Guidance"/> の補助。認証交換の内容は含まない）。
/// </param>
public sealed record EmailNotificationTestResult(
    bool Succeeded,
    string Guidance,
    string? ServerResponse,
    IReadOnlyList<string> RejectedRecipients);
