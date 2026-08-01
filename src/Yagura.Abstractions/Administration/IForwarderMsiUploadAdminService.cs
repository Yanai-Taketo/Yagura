namespace Yagura.Abstractions.Administration;

/// <summary>
/// フォワーダ MSI アップロード機能の opt-in（<c>Admin:ForwarderKit:MsiUpload:Enabled</c>）を
/// 管理画面から切り替える契約（ADR-0021 決定 4 = 委任 2）。
/// </summary>
/// <remarks>
/// <para>
/// <b>設定動線を GUI へ寄せる</b>: 機能到達までに GUI・設定ファイル手編集・ターミナルの
/// 3 操作面をまたぐことを問題視した（ADR-0021）。本サービスは手編集
/// （手順 4）を GUI へ移す——残るターミナル操作は ACE 付与のみになる（書き込み口の物理的開閉を
/// OS レベルの身元保証に残す構造は ADR-0020 決定 2 の核心であり、UI 化しない）。
/// </para>
/// <para>
/// <b>認可はアップロード操作と同格</b>（ADR-0021 決定 4）: 有効化・無効化は「MSI 書き込み口を
/// 出現させる/消す」操作であり、実際にサインインした管理セッションのみが実行できる
/// （判定は <c>AdminAuthenticationExtensions.IsForwarderMsiUploadOperationAllowed</c> の単一実装を
/// 画面が呼び、結果を <paramref name="operatorIsUploadOperationAuthenticated"/> で渡す）。
/// 無認証 loopback から opt-in を反転できてはならない。
/// </para>
/// <para>
/// <b>有効化には切替時点検を伴う</b>（ADR-0021 決定 1 の事前仕込み対処）: 有効化時は
/// <see cref="GetStatusAsync"/> が返す既存アプリアカウントの情報（有無・作成/最終変更時刻）を
/// 画面が提示し、管理者の確認（<paramref name="accountInventoryAcknowledged"/>）を必須とする。
/// 無効化・変更なしでは確認を求めない。
/// </para>
/// </remarks>
public interface IForwarderMsiUploadAdminService : IYaguraWriteService
{
    /// <summary>現在の opt-in 状態・前提条件の充足・切替時点検に提示する情報を返す。</summary>
    Task<ForwarderMsiUploadSettingStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// opt-in を切り替える（単一の確定操作。反映はサービス再起動——エンドポイントの条件付き登録が
    /// 起動時に固定されるため）。
    /// </summary>
    /// <param name="enabled">有効化するなら <see langword="true"/>。</param>
    /// <param name="accountInventoryAcknowledged">
    /// 有効化時の切替時点検（既存アプリアカウントが自分の管理下にあることの確認）。
    /// 有効化かつ既存アカウントが存在する場合に <see langword="true"/> でなければ拒否する。
    /// </param>
    /// <param name="operatorAddress">操作者の接続元アドレス（監査記録用）。</param>
    /// <param name="operatorScheme">操作者の認証方式。</param>
    /// <param name="operatorPrincipal">操作者の認証済み利用者名。</param>
    /// <param name="operatorIsUploadOperationAuthenticated">
    /// 操作者がアップロード系操作の専用認可判定を通過しているか（ADR-0021 決定 4）。
    /// <see langword="false"/> は <see cref="WizardValidationException"/> で拒否する。
    /// </param>
    /// <exception cref="WizardValidationException">
    /// 未サインイン、前提条件（認証方式が最低 1 つ有効）の不成立、または有効化時の切替時点検が
    /// 未確認の場合。
    /// </exception>
    Task<ForwarderMsiUploadSettingResult> ConfigureAsync(
        bool enabled,
        bool accountInventoryAcknowledged = false,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        bool operatorIsUploadOperationAuthenticated = false,
        CancellationToken cancellationToken = default);
}

/// <summary>アップロード opt-in の現在状態（表示用。ADR-0021 委任 2）。</summary>
/// <param name="Enabled">opt-in の永続値。</param>
/// <param name="WindowsAuthEnabled">Windows 統合認証の有効/無効（前提条件の表示用）。</param>
/// <param name="AppAuthEnabled">アプリ独自認証の有効/無効（同上）。</param>
/// <param name="HasAppAccount">アプリ独自認証の管理者アカウントが存在するか（切替時点検の提示用）。</param>
/// <param name="AppAccountUsername">存在する場合のユーザー名。</param>
/// <param name="AppAccountCreatedAtUtc">
/// アカウントの作成時刻。スキーマ v3 より前に作成されたアカウントでは <see langword="null"/>
/// （= 不明。画面は「不明」と表示する）。
/// </param>
/// <param name="AppAccountUpdatedAtUtc">直近の変更時刻（同上）。</param>
/// <param name="AppAccountLastLoginAtUtc">
/// 直近の成功ログイン時刻。**スキーマ v3 より前から存在するアカウントでも
/// 値が入る**ため、作成・変更時刻が不明なアップグレード環境で点検の唯一の手がかりになる
/// （「使われていないのに存在するアカウント」の判断材料）。未ログインなら <see langword="null"/>。
/// </param>
public sealed record ForwarderMsiUploadSettingStatus(
    bool Enabled,
    bool WindowsAuthEnabled,
    bool AppAuthEnabled,
    bool HasAppAccount,
    string? AppAccountUsername,
    DateTimeOffset? AppAccountCreatedAtUtc,
    DateTimeOffset? AppAccountUpdatedAtUtc,
    DateTimeOffset? AppAccountLastLoginAtUtc = null);

/// <summary><see cref="IForwarderMsiUploadAdminService.ConfigureAsync"/> の結果。</summary>
/// <param name="Changed">値が実際に変わったか（false = no-op。保存も監査も行っていない）。</param>
/// <param name="RequiredEffect">
/// 反映に必要なアクション。変更ありは常に <see cref="ConfigurationApplyEffect.RestartRequired"/>
/// （= サービス再起動 1 回 ≒ 受信断の窓。ADR-0021 決定 4 が「隠さない」と定めた代償）。
/// </param>
/// <param name="Status">保存後（no-op なら現在）の状態。</param>
public sealed record ForwarderMsiUploadSettingResult(
    bool Changed,
    ConfigurationApplyEffect RequiredEffect,
    ForwarderMsiUploadSettingStatus Status);
