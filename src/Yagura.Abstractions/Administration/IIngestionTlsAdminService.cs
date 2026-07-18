namespace Yagura.Abstractions.Administration;

/// <summary>
/// TLS 受信（syslog over TLS。RFC 5425。opt-in。Issue #137）の設定を管理 UI から
/// 保存・検証する契約（ADR-0019 決定 1・2）。
/// </summary>
/// <remarks>
/// <para>
/// <b>対象キー（新設キーゼロ。ADR-0019 決定 1）</b>: <c>Ingestion:Tls:Enabled</c>・
/// <c>Ingestion:Tls:Port</c>・<c>Ingestion:Tls:CertificateThumbprint</c> の既存 3 キー
/// （configuration.md §8 既出・いずれも反映方式はサービス再起動）のみを扱う。
/// <b><c>Ingestion:Tls:BindAddress</c> は意図的に対象外</b>——受信系の bind 先 UI は UDP/TCP も
/// 含めて未対応であり、bind 先の UI 化は ADR-0019 のスコープ外（手編集のまま残す）。
/// </para>
/// <para>
/// <b>管理リモート HTTPS（<see cref="IAdminRemoteAccessAdminService"/>）との関係</b>: 証明書ストアの
/// 列挙（<see cref="ICertificateStoreReader"/>）と証明書の解決は<b>実装を共有し二重実装しない</b>
/// （ADR-0019 決定 1）。一方で本契約は別契約・別画面とする——用途が違い（送信元機器 → Yagura の
/// syslog 受信 / ブラウザ → Yagura の管理画面閲覧）、後述のとおり<b>期限切れ証明書の扱いが逆</b>で
/// あるため（ADR-0019 選択肢 (c) の却下理由）。両キーは独立であり、同一証明書を流用する場合は
/// 両方に指定する（configuration.md §8）。
/// </para>
/// <para>
/// <b>保存前 fail-closed 検証（ADR-0019 決定 2）</b>: 拒否する項目と警告して通す項目を
/// <b>明確に分けて宣言する</b>（実装で拒否側へ倒される事故を防ぐため、同じ列挙に混ぜない）。
/// </para>
/// <list type="table">
/// <item>
/// <term>拒否（<see cref="WizardValidationException"/>）</term>
/// <description>
/// <c>Enabled=true</c> なのに拇印未設定 / 証明書が見つからない / <b>秘密鍵にアクセスできない</b> /
/// serverAuth EKU 不適合。
/// </description>
/// </item>
/// <item>
/// <term>警告して通す</term>
/// <description>
/// <b>期限切れ</b>（<see cref="IngestionTlsConfigureResult.ExpiredWarning"/>）。
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>この 2 つの分類は管理リモート HTTPS と逆向きに割れている</b>（どちらも security.md が
/// 確定した非対称であり、実装の都合ではない）:
/// </para>
/// <list type="table">
/// <item><term>期限切れ</term><description>管理 HTTPS = <b>拒否</b> / TLS 受信 = <b>警告して通す</b></description></item>
/// <item><term>秘密鍵が読めない</term><description>管理 HTTPS = <b>警告して通す</b> / TLS 受信 = <b>拒否</b></description></item>
/// </list>
/// <para>
/// 理由は「失敗したときに何が起きるか」が違うため。管理 HTTPS は失敗しても loopback 経由で
/// 管理面へ逃げられる（縮小継続）。TLS 受信は<b>秘密鍵がなければハンドシェイクが成立せず
/// 受信チャネルが丸ごと立たない</b>一方、<b>期限切れでも受信は継続できる</b>（検証して接続を
/// 拒否するかは送信側の判断）。したがって「受信が止まる側」を拒否し「止まらない側」を警告に倒す。
/// </para>
/// <para>
/// <b>期限切れを通すのは管理 HTTPS との確定した非対称である</b>（security.md §6）。TLS 受信は
/// 期限切れでも受信を止めない——証明書を検証して接続を拒否するかは<b>送信側の判断</b>であり、
/// 受信側が先回りして受信を止めると「送信元がサイレントに脱落する」事故を製品自ら起こすため。
/// 起動時の TLS 証明書解決も期限を判定に使っていない（<c>Program.cs</c>）ので、本契約の
/// 「警告して通す」は既存の起動時挙動と一致している（決定 2「UI 事前検証と起動時検証の乖離ゼロ」）。
/// 管理リモート HTTPS 側は逆に期限切れを拒否する（起動時に bind をスキップするため揃えている）。
/// </para>
/// <para>
/// <b>層配置</b>: 契約は <c>Yagura.Abstractions.Administration</c>・実体は <c>Yagura.Host</c>・
/// 結線は <c>Program.cs</c>（<see cref="IAdminRemoteAccessAdminService"/> と同一パターン。
/// architecture.md §1.1「UI は Host アセンブリを参照できない」）。
/// </para>
/// </remarks>
public interface IIngestionTlsAdminService : IYaguraWriteService
{
    /// <summary>
    /// 永続値（<c>yagura.json</c>）から、対象 3 キーの現在値を返す（設定画面の初期表示用）。
    /// </summary>
    Task<IngestionTlsStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// TLS 受信の設定を、保存前 fail-closed 検証（ADR-0019 決定 2）を通したうえで保存し、
    /// 変更があれば監査記録する（同 決定 5。イベント ID 2020）。変更がゼロなら保存も監査も
    /// しない（<see cref="IngestionTlsConfigureResult.ChangedKeys"/> が空の no-op を返す）。
    /// </summary>
    /// <param name="enabled">TLS 受信の有効/無効（<c>Ingestion:Tls:Enabled</c>）。</param>
    /// <param name="certificateThumbprint">
    /// 証明書の拇印（SHA-1・16 進 40 桁。空白・コロン・ハイフン区切りは正規化して受理する。
    /// 未設定にする場合は <see langword="null"/> または空文字）。
    /// </param>
    /// <param name="port">
    /// TLS 受信のポート（1〜65535 の 10 進表記。未設定 = 既定 6514（RFC 5425 の標準ポート）を
    /// 使う場合は <see langword="null"/> または空文字）。
    /// </param>
    /// <param name="operatorAddress">操作者の接続元アドレス（監査記録用）。</param>
    /// <param name="operatorScheme">操作者の認証方式（ADR-0010 決定 6。未認証では <see langword="null"/>）。</param>
    /// <param name="operatorPrincipal">操作者の認証済み利用者名（同上）。</param>
    /// <exception cref="WizardValidationException">
    /// 上記「拒否」の項目に該当する場合。メッセージは画面にそのまま表示できる日本語で、
    /// 是正手順への誘導を含む。<b>期限切れは拒否しない</b>（警告フラグで通す）。
    /// </exception>
    /// <remarks>
    /// 設定ファイルが読み込み後に外部変更されていた場合（楽観競合。configuration.md §3）は、
    /// 実装側の競合例外（<c>ConfigurationConflictException</c>）をそのまま伝播する。
    /// </remarks>
    Task<IngestionTlsConfigureResult> ConfigureAsync(
        bool enabled,
        string? certificateThumbprint,
        string? port,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default);
}

/// <summary>TLS 受信設定の現在状態（表示用。すべて永続値由来）。</summary>
/// <param name="Enabled">TLS 受信の有効/無効（<c>Ingestion:Tls:Enabled</c>）。</param>
/// <param name="CertificateThumbprint">
/// 証明書拇印（<c>Ingestion:Tls:CertificateThumbprint</c>）。形式が有効なら正規化済み
/// （大文字・16 進 40 桁——<see cref="CertificateCandidate.Thumbprint"/> とそのまま照合できる）、
/// 不正な形式の永続値はそのまま返す（画面で「不正な値が設定されている」ことを示せるように）。
/// 未設定は <see langword="null"/>。
/// </param>
/// <param name="Port">
/// TLS 受信のポート（<c>Ingestion:Tls:Port</c> の永続値。未設定 = 既定 6514 は <see langword="null"/>）。
/// </param>
public sealed record IngestionTlsStatus(
    bool Enabled,
    string? CertificateThumbprint,
    string? Port);

/// <summary><see cref="IIngestionTlsAdminService.ConfigureAsync"/> の結果。</summary>
/// <param name="ChangedKeys">
/// 実際に値が変わったキーの一覧（永続値の実効値との比較。変更ゼロ = no-op なら空で、
/// 保存も監査も行われていない）。
/// </param>
/// <param name="RequiredEffect">
/// 変更の反映に必要なアクション（configuration.md §3。対象 3 キーはすべてサービス再起動のため、
/// 変更ありなら <see cref="ConfigurationApplyEffect.RestartRequired"/>。no-op は反映不要のため
/// <see cref="ConfigurationApplyEffect.Immediate"/>）。
/// </param>
/// <param name="ExpiredWarning">
/// 選択された証明書が期限切れ（または有効期間前）の場合 <see langword="true"/>。
/// <b>保存は成功する</b>——TLS 受信は期限切れでも受信を止めない（security.md §6。ADR-0019 決定 2）。
/// UI はこのとき、送信側が検証を拒否してログが届かなくなりうること、および期限接近/使用不能の
/// 能動通知（1017/1018）は有効な証明書へ差し替えるまで出続けることを警告として表示する。
/// <b>本結果に「秘密鍵を読めない」警告が無いのは意図的である</b>——TLS 受信では秘密鍵に
/// アクセスできない証明書は<b>拒否</b>する（管理リモート HTTPS が警告に留めるのと逆。
/// 秘密鍵なしでは TLS ハンドシェイクが成立せず受信チャネルが丸ごと立たないため）。
/// </param>
/// <param name="Status">保存後（no-op なら現在）の状態。</param>
public sealed record IngestionTlsConfigureResult(
    IReadOnlyList<string> ChangedKeys,
    ConfigurationApplyEffect RequiredEffect,
    bool ExpiredWarning,
    IngestionTlsStatus Status);
