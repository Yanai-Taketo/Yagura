namespace Yagura.Abstractions.Administration;

/// <summary>
/// 管理 UI 認証（ADR-0010 Phase 1）の設定・最初の管理者アカウントを扱う契約。
/// </summary>
/// <remarks>
/// <para>
/// <b>「セットアップウィザードの流儀に従う」（ADR-0010 決定 3）が意味する設計</b>: 初期セットアップ
/// ウィザード（<see cref="ISetupWizardService"/>）のような複数ステップの確定セッションは
/// 持たない——本サービスが扱う項目（認証方式の有効化・Kerberos-only・loopback 認証 opt-in・
/// 最初の管理者アカウント）は相互依存が薄く、単一の確認画面 + 単一の適用操作で足りる規模の
/// ため（規模に見合った実装。conventions.md の依存最小化の判断規準と同じ姿勢）。ただし
/// 「読み込み → 変更 → 検証 → 保存」（configuration.md §3）・監査記録（security.md §4.1）・
/// 書き込み系マーカー（<see cref="IYaguraWriteService"/>）という共通規約は同様に踏襲する。
/// </para>
/// <para>
/// <b>管理リスナ限定・loopback 中セットアップ</b>: 本サービスの画面（<c>/admin/auth-setup</c>）は
/// 他の管理画面と同じ帰属検査（<c>AdminScreenLayout</c>）の対象であり、既定の loopback 限定
/// アクセス（認証 opt-in 前）の間に最初の管理者アカウントを設定する運用を前提とする
/// （ADR-0010 決定 3「最初の管理者アカウントは...物理到達性による信頼が既に効いている状態で
/// オペレーターが対話的に設定する」）。
/// </para>
/// </remarks>
public interface IAdminAuthenticationAdminService : IYaguraWriteService
{
    /// <summary>現在の管理 UI 認証設定とアプリ独自認証アカウントの状態を返す。</summary>
    Task<AdminAuthenticationStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 管理 UI 認証設定を変更し、必要なら最初の管理者アカウントを作成/変更する（単一の確定操作）。
    /// </summary>
    /// <param name="windowsAuthEnabled">Windows 統合認証（Negotiate）の有効/無効。</param>
    /// <param name="kerberosOnly">Kerberos-only モード（NTLM 無効化 opt-in）。</param>
    /// <param name="appAuthEnabled">アプリ独自 ID/パスワード認証の有効/無効。</param>
    /// <param name="requireForLoopback">loopback アクセスにも認証を課す opt-in（ADR-0010 決定 1）。</param>
    /// <param name="newAppUsername">
    /// 最初の管理者アカウントのユーザー名（作成/変更しない場合は <see langword="null"/>）。
    /// <paramref name="newAppPassword"/> と両方指定した場合のみアカウントを作成/変更する。
    /// </param>
    /// <param name="newAppPassword">最初の管理者アカウントのパスワード（平文。保存前にハッシュ化する）。</param>
    /// <param name="operatorAddress">操作者の接続元アドレス（監査記録用）。</param>
    /// <param name="operatorScheme">操作者の認証方式（ADR-0010 決定 6。未認証では <see langword="null"/>）。</param>
    /// <param name="operatorPrincipal">操作者の認証済み利用者名（同上）。</param>
    /// <exception cref="WizardValidationException">
    /// fail-closed 不変条件（ADR-0010 決定 1）に反する組み合わせ、またはアプリ独自認証を
    /// 有効化しようとしているのにアカウントが 1 件も存在しない（本呼び出しでも作成しない）場合。
    /// </exception>
    Task<AdminAuthenticationStatus> ConfigureAsync(
        bool windowsAuthEnabled,
        bool kerberosOnly,
        bool appAuthEnabled,
        bool requireForLoopback,
        string? newAppUsername,
        string? newAppPassword,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default);
}

/// <summary>管理 UI 認証の現在状態（表示用）。</summary>
/// <param name="WindowsAuthEnabled">Windows 統合認証の有効/無効。</param>
/// <param name="KerberosOnly">Kerberos-only モードの有効/無効。</param>
/// <param name="AppAuthEnabled">アプリ独自認証の有効/無効。</param>
/// <param name="RequireForLoopback">loopback 認証 opt-in の有効/無効。</param>
/// <param name="HasAppAccount">アプリ独自認証の管理者アカウントが存在するか。</param>
/// <param name="AppAccountUsername">存在する場合のユーザー名（存在しなければ <see langword="null"/>）。</param>
public sealed record AdminAuthenticationStatus(
    bool WindowsAuthEnabled,
    bool KerberosOnly,
    bool AppAuthEnabled,
    bool RequireForLoopback,
    bool HasAppAccount,
    string? AppAccountUsername);
