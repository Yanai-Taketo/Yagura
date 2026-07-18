namespace Yagura.Abstractions.Administration;

/// <summary>
/// 管理リスナのリモートバインド + HTTPS（ADR-0010 Phase 2 決定 1・4）の設定を管理 UI から
/// 保存・検証する契約（ADR-0012 決定 1・4）。
/// </summary>
/// <remarks>
/// <para>
/// <b>対象キー（新設キーゼロ。ADR-0012 決定 6）</b>: <c>Admin:RemoteBinding:Enabled</c>・
/// <c>Admin:Https:Enabled</c>・<c>Admin:Https:CertificateThumbprint</c>・<c>Admin:Https:Port</c> の
/// 既存 4 キー（configuration.md §8 既出・いずれも反映方式はサービス再起動）のみを扱う。
/// </para>
/// <para>
/// <b>層配置</b>: 契約は <c>Yagura.Abstractions.Administration</c>・実体は <c>Yagura.Host</c>・
/// 結線は <c>Program.cs</c>（<see cref="IAdminAuthenticationAdminService"/> と同一パターン。
/// architecture.md §1.1「UI は Host アセンブリを参照できない」。ADR-0012 影響範囲）。
/// 証明書ストアの読み出し（列挙）は別契約 <see cref="ICertificateStoreReader"/>（副作用なし）が
/// 担い、本契約は書き込み系（<see cref="IYaguraWriteService"/>）として保存・保存前検証のみを担う
/// （ADR-0012 決定 3「read-only 列挙・読取検証と書き込みは別契約に分離する」）。
/// </para>
/// <para>
/// <b>保存前 fail-closed 検証（ADR-0012 決定 4）</b>: 起動時 fail-closed 検証（イベント ID 1012 =
/// 起動拒否・1013 = リモート HTTPS bind の縮小継続）で初めて気づく構成不備を、保存時点へ前倒しして
/// 拒否する。<see cref="IAdminAuthenticationAdminService.ConfigureAsync"/> と同じ
/// 「UI 層で親切に拒否（<see cref="WizardValidationException"/>）→ 起動時検証が最終防衛線」の
/// 二段構えであり、不変条件の判定は<b>画面上の値ではなく永続化された現在値</b>（<c>yagura.json</c>）に
/// 対して行う（決定 4「関与する全画面が同一の不変条件を共有する」）。
/// </para>
/// </remarks>
public interface IAdminRemoteAccessAdminService : IYaguraWriteService
{
    /// <summary>
    /// 永続値（<c>yagura.json</c>）から、対象 4 キーの現在値と fail-closed 判定に必要な
    /// 認証の有効状態を返す（設定画面の初期表示用）。
    /// </summary>
    Task<AdminRemoteAccessStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 管理リモート HTTPS の設定を、保存前 fail-closed 検証（ADR-0012 決定 4）を通したうえで
    /// 保存し、変更があったキー群に応じて監査記録する（同 決定 7。イベント ID 2011/2012）。
    /// 変更がゼロなら保存も監査もしない（<see cref="AdminRemoteAccessConfigureResult.ChangedKeys"/>
    /// が空の no-op を返す）。
    /// </summary>
    /// <param name="remoteBindingEnabled">管理リスナのリモートバインドの有効/無効。</param>
    /// <param name="httpsEnabled">管理リスナのリモート HTTPS の有効/無効。</param>
    /// <param name="certificateThumbprint">
    /// 証明書の拇印（SHA-1・16 進 40 桁。空白・コロン・ハイフン区切りは正規化して受理する。
    /// 未設定にする場合は <see langword="null"/> または空文字）。
    /// </param>
    /// <param name="httpsPort">
    /// リモート HTTPS のポート（1〜65535 の 10 進表記。未設定 = 既定 8516 を使う場合は
    /// <see langword="null"/> または空文字）。
    /// </param>
    /// <param name="operatorAddress">操作者の接続元アドレス（監査記録用）。</param>
    /// <param name="operatorScheme">操作者の認証方式（ADR-0010 決定 6。未認証では <see langword="null"/>）。</param>
    /// <param name="operatorPrincipal">操作者の認証済み利用者名（同上）。</param>
    /// <exception cref="WizardValidationException">
    /// 保存前 fail-closed 検証（ADR-0012 決定 4）に反する場合: リモートバインド有効化の前提
    /// （永続値で認証方式が有効・HTTPS 有効・拇印指定・有効なポート）を欠く、拇印・ポートの形式が
    /// 不正、または指定された証明書が起動時検証（イベント ID 1013 と同一コード）で使用不能
    /// （見つからない・秘密鍵なし・期限切れ・serverAuth EKU 不適合）な場合。
    /// メッセージは画面にそのまま表示できる日本語で、是正手順への誘導を含む。
    /// </exception>
    /// <remarks>
    /// 設定ファイルが読み込み後に外部変更されていた場合（楽観競合。configuration.md §3）は、
    /// <see cref="IAdminAuthenticationAdminService.ConfigureAsync"/> と同じく実装側の競合例外
    /// （<c>ConfigurationConflictException</c>——再読み込みからのやり直しを促すメッセージを持つ）を
    /// そのまま伝播する。
    /// </remarks>
    Task<AdminRemoteAccessConfigureResult> ConfigureAsync(
        bool remoteBindingEnabled,
        bool httpsEnabled,
        string? certificateThumbprint,
        string? httpsPort,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default);
}

/// <summary>管理リモート HTTPS 設定の現在状態（表示用。すべて永続値由来）。</summary>
/// <param name="RemoteBindingEnabled">リモートバインドの有効/無効（<c>Admin:RemoteBinding:Enabled</c>）。</param>
/// <param name="HttpsEnabled">リモート HTTPS の有効/無効（<c>Admin:Https:Enabled</c>）。</param>
/// <param name="CertificateThumbprint">
/// 証明書拇印（<c>Admin:Https:CertificateThumbprint</c>）。形式が有効なら正規化済み
/// （大文字・16 進 40 桁——<see cref="CertificateCandidate.Thumbprint"/> とそのまま照合できる）、
/// 不正な形式の永続値はそのまま返す（画面で「不正な値が設定されている」ことを示せるように）。
/// 未設定は <see langword="null"/>。
/// </param>
/// <param name="HttpsPort">リモート HTTPS のポート（<c>Admin:Https:Port</c> の永続値。未設定 = 既定 8516 は <see langword="null"/>）。</param>
/// <param name="WindowsAuthEnabled">Windows 統合認証の有効/無効（fail-closed 不変条件の表示用。永続値）。</param>
/// <param name="AppAuthEnabled">アプリ独自認証の有効/無効（同上）。</param>
public sealed record AdminRemoteAccessStatus(
    bool RemoteBindingEnabled,
    bool HttpsEnabled,
    string? CertificateThumbprint,
    string? HttpsPort,
    bool WindowsAuthEnabled,
    bool AppAuthEnabled);

/// <summary><see cref="IAdminRemoteAccessAdminService.ConfigureAsync"/> の結果。</summary>
/// <param name="ChangedKeys">
/// 実際に値が変わったキーの一覧（永続値の実効値との比較。変更ゼロ = no-op なら空で、
/// 保存も監査も行われていない）。
/// </param>
/// <param name="RequiredEffect">
/// 変更の反映に必要なアクション（configuration.md §3。対象 4 キーはすべてサービス再起動のため、
/// 変更ありなら <see cref="ConfigurationApplyEffect.RestartRequired"/>。no-op は反映不要のため
/// <see cref="ConfigurationApplyEffect.Immediate"/>）。
/// </param>
/// <param name="PrivateKeyUnreadableWarning">
/// 選択された証明書の秘密鍵を現在の実行アカウント（サービスアカウント）が読み取れない場合
/// <see langword="true"/>（ADR-0012 決定 3 = (b)。保存自体は成功する——起動時も縮小継続であり
/// サービス全体は止まらないため拒否にはしない。UI は <c>certlm.msc</c> での読取権限付与
/// （CF-D2）へ誘導する）。
/// </param>
/// <param name="Status">保存後（no-op なら現在）の状態。</param>
public sealed record AdminRemoteAccessConfigureResult(
    IReadOnlyList<string> ChangedKeys,
    ConfigurationApplyEffect RequiredEffect,
    bool PrivateKeyUnreadableWarning,
    AdminRemoteAccessStatus Status);
