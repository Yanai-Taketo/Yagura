namespace Yagura.Abstractions.Administration;

/// <summary>
/// 閲覧 UI の HTTPS（ADR-0022。opt-in。8514 同一ポート切替）の設定を管理 UI から
/// 保存・検証する契約（決定 3）。
/// </summary>
/// <remarks>
/// <para>
/// <b>対象キー</b>: <c>Viewer:Https:Enabled</c>・<c>Viewer:Https:CertificateThumbprint</c> の 2 キー
/// （configuration.md §8。いずれも反映方式はサービス再起動）。ポートキーは存在しない——閲覧
/// リスナは <c>Viewer:HttpPort</c>（既定 8514）のまま HTTPS へ切り替わる（決定 1）。
/// </para>
/// <para>
/// <b>証明書選択 UI の 3 回目の再利用（決定 3）</b>: 列挙（<see cref="ICertificateStoreReader"/>）・
/// 証明書の解決・EKU 判定・秘密鍵の読取検証は管理リモート HTTPS
/// （<see cref="IAdminRemoteAccessAdminService"/>）・TLS 受信（<see cref="IIngestionTlsAdminService"/>）と
/// 実装を共有し三重実装しない。契約・画面は分ける（用途の書き分け——閲覧画面〔LAN のブラウザ →
/// Yagura〕用）。
/// </para>
/// <para>
/// <b>保存前 fail-closed 検証（決定 4——ADR-0019 決定 2 の二分宣言の型）</b>:
/// </para>
/// <list type="table">
/// <item>
/// <term>拒否（<see cref="WizardValidationException"/>）</term>
/// <description>
/// <c>Enabled=true</c> なのに拇印未設定 / 拇印の形式不正 / 証明書が見つからない /
/// <b>期限切れ・有効期間前</b>（管理 HTTPS と同じ拒否側——期限切れで<b>停止する</b>面は保存を拒否し、
/// <b>継続する</b>面〔TLS 受信〕は警告で通す。ADR-0019 決定 2 の確定済み非対称） /
/// <b>秘密鍵にアクセスできない</b>（読めないまま再起動すると閲覧リスナが縮小継続で開かず、
/// LAN からの閲覧が止まる——「止まる側を拒否に倒す」。ADR-0022 決定 4 の拒否側宣言） /
/// serverAuth EKU 不適合。
/// </description>
/// </item>
/// <item>
/// <term>警告して通す</term>
/// <description>
/// <b>SAN ホスト名整合（決定 4——本 ADR 固有の拡張）</b>: 証明書の SAN にサーバ自身の
/// FQDN・NetBIOS 短名が含まれない場合。保存は通す——利用者が閲覧に使う名前をサーバは断定
/// できないため、助言に留める（<see cref="ViewerHttpsConfigureResult.SanInspection"/>）。
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>層配置</b>: 契約は <c>Yagura.Abstractions.Administration</c>・実体は <c>Yagura.Host</c>・
/// 結線は <c>Program.cs</c>（<see cref="IIngestionTlsAdminService"/> と同一パターン）。
/// </para>
/// </remarks>
public interface IViewerHttpsAdminService : IYaguraWriteService
{
    /// <summary>
    /// 永続値（<c>yagura.json</c>）から、対象 2 キーの現在値と画面表示に必要な周辺情報
    /// （閲覧ポート・管理 HTTPS 拇印〔決定 9 のコピー動線用〕・メール通知の有効状態
    /// 〔決定 6 の保存時案内 (d) 用〕）を返す。
    /// </summary>
    Task<ViewerHttpsStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 閲覧 HTTPS の設定を、保存前 fail-closed 検証を通したうえで保存し、変更があれば監査記録する
    /// （決定 10。イベント ID 2029）。変更がゼロなら保存も監査もしない（no-op）。
    /// </summary>
    /// <param name="enabled">閲覧 HTTPS の有効/無効（<c>Viewer:Https:Enabled</c>）。</param>
    /// <param name="certificateThumbprint">
    /// 証明書の拇印（SHA-1・16 進 40 桁。空白・コロン・ハイフン区切りは正規化して受理する。
    /// 未設定にする場合は <see langword="null"/> または空文字）。<b>決定 9 のコピー動線で転写された
    /// 拇印も本引数を通り、通常の選択と同一の検証を受ける</b>（コピーは検査のバイパス経路にならない）。
    /// </param>
    /// <param name="operatorAddress">操作者の接続元アドレス（監査記録用）。</param>
    /// <param name="operatorScheme">操作者の認証方式（ADR-0010 決定 6。未認証では <see langword="null"/>）。</param>
    /// <param name="operatorPrincipal">操作者の認証済み利用者名（同上）。</param>
    /// <exception cref="WizardValidationException">
    /// 上記「拒否」の項目に該当する場合。メッセージは画面にそのまま表示できる日本語で、
    /// 是正手順への誘導を含む。<b>SAN 不整合は拒否しない</b>（検査結果で通す——決定 4）。
    /// </exception>
    Task<ViewerHttpsConfigureResult> ConfigureAsync(
        bool enabled,
        string? certificateThumbprint,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定拇印の証明書の SAN ホスト名整合検査（決定 4）だけを実行する（保存はしない。副作用なし）。
    /// 保存後も設定画面で現在の検査結果を確認できるようにするための読み出し面（決定 4——
    /// 「保存時一度きりにしない」）。証明書が解決できない場合は <see langword="null"/>
    /// （画面は縮小継続の可能性として別途表示する）。
    /// </summary>
    Task<ViewerHttpsSanInspection?> InspectCertificateAsync(
        string certificateThumbprint, CancellationToken cancellationToken = default);
}

/// <summary>閲覧 HTTPS 設定の現在状態（表示用。すべて永続値由来）。</summary>
/// <param name="Enabled">閲覧 HTTPS の有効/無効（<c>Viewer:Https:Enabled</c>）。</param>
/// <param name="CertificateThumbprint">
/// 証明書拇印（<c>Viewer:Https:CertificateThumbprint</c>）。形式が有効なら正規化済み、
/// 不正な形式の永続値はそのまま返す（画面で「不正な値が設定されている」ことを示せるように）。
/// 未設定は <see langword="null"/>。
/// </param>
/// <param name="ViewerPort">
/// 閲覧リスナのポート（<c>Viewer:HttpPort</c> の永続値。未設定 = 既定 8514 は <see langword="null"/>）。
/// 決定 6 の新 URL 案内の組み立てに使う（環境変数 <c>YAGURA_HTTP_PORT</c> による上書きは
/// 永続値からは見えない——画面はその旨を注記する）。
/// </param>
/// <param name="AdminHttpsCertificateThumbprint">
/// 管理リモート HTTPS の証明書拇印（<c>Admin:Https:CertificateThumbprint</c>。正規化済み。
/// 未設定・不正形式は <see langword="null"/>）。決定 9 の「管理と同じ証明書を使う」コピー動線の
/// 表示判定と転写元に使う（設定済みの場合のみボタンを表示する）。
/// </param>
/// <param name="EmailNotificationEnabled">
/// メール通知（<c>Notification:Email:Enabled</c>）が有効か。決定 6 の保存時案内 (d)——無効の場合
/// 「期限切れの事前警告はイベントログにしか届かない」の指摘とメール通知設定への導線——に使う。
/// </param>
public sealed record ViewerHttpsStatus(
    bool Enabled,
    string? CertificateThumbprint,
    string? ViewerPort,
    string? AdminHttpsCertificateThumbprint,
    bool EmailNotificationEnabled);

/// <summary><see cref="IViewerHttpsAdminService.ConfigureAsync"/> の結果。</summary>
/// <param name="ChangedKeys">
/// 実際に値が変わったキーの一覧（変更ゼロ = no-op なら空で、保存も監査も行われていない）。
/// </param>
/// <param name="RequiredEffect">
/// 変更の反映に必要なアクション（対象 2 キーはすべてサービス再起動のため、変更ありなら
/// <see cref="ConfigurationApplyEffect.RestartRequired"/>。no-op は <see cref="ConfigurationApplyEffect.Immediate"/>）。
/// </param>
/// <param name="SanInspection">
/// 選択された証明書の SAN ホスト名整合検査の結果（決定 4。拇印未設定なら <see langword="null"/>）。
/// 不整合でも<b>保存は成功している</b>——画面は警告と「警告なしでアクセスできる URL」
/// （<see cref="ViewerHttpsSanInspection.CertificateDnsNames"/> から組み立てる）を表示する。
/// </param>
/// <param name="Status">保存後（no-op なら現在）の状態。</param>
public sealed record ViewerHttpsConfigureResult(
    IReadOnlyList<string> ChangedKeys,
    ConfigurationApplyEffect RequiredEffect,
    ViewerHttpsSanInspection? SanInspection,
    ViewerHttpsStatus Status);

/// <summary>
/// SAN ホスト名整合の助言検査の結果（ADR-0022 決定 4）。<b>検査対象はホスト名（dNSName）のみで、
/// IP アドレスでのアクセスは検査対象外</b>（IP で開くと警告が出る——委任 4 で IP SAN の扱いを判断
/// するまでの確定範囲）。複数 NIC・エイリアス（CNAME）も検査の射程外——検査は助言であり網羅を
/// 約束しない（委任 4）。
/// </summary>
/// <param name="RequiredServerNames">
/// 検査したサーバ自身の名前（NetBIOS 短名・FQDN。ドメイン非参加等で FQDN が短名と一致する場合は
/// 1 件のみ）。
/// </param>
/// <param name="MissingServerNames">
/// <see cref="RequiredServerNames"/> のうち、証明書の SAN（dNSName。ワイルドカード展開を含む）で
/// カバーされない名前。空なら整合（この証明書で短名・FQDN いずれのアクセスも証明書名の不一致
/// 警告にならない見込み——ブラウザ実機検証は lab。委任 4）。
/// </param>
/// <param name="CertificateDnsNames">
/// 証明書の SAN に含まれる dNSName の一覧（ワイルドカードを含む・原文のまま）。画面はここから
/// 「警告なしでアクセスできる URL」を組み立てて具体的に提示する（決定 4 の文言方針）。
/// </param>
/// <param name="HasSanExtension">
/// 証明書が SAN 拡張を持つか。<see langword="false"/> の場合、現代のブラウザは CN を見ないため
/// どの名前でも証明書名の不一致警告になる（<see cref="MissingServerNames"/> は全件になる）。
/// </param>
public sealed record ViewerHttpsSanInspection(
    IReadOnlyList<string> RequiredServerNames,
    IReadOnlyList<string> MissingServerNames,
    IReadOnlyList<string> CertificateDnsNames,
    bool HasSanExtension)
{
    /// <summary>サーバ自身の全名前が SAN でカバーされているか。</summary>
    public bool IsSatisfied => MissingServerNames.Count == 0;
}
