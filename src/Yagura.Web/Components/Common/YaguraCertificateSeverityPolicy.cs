namespace Yagura.Web.Components.Common;

/// <summary>
/// <see cref="YaguraCertificateSelectionPanel"/> のバッジ・注記の重大度と文言。証明書設定 3 画面
/// （閲覧 HTTPS・TLS 受信・管理リモート HTTPS）は期限切れ／秘密鍵読取不可の Error/Warning 判定が
/// 意図的に画面ごとに異なる（閲覧・管理 HTTPS は期限切れ = 保存拒否、TLS 受信は「受信を止めない」
/// 原則により警告に留める、等の製品仕様）ため、パネル側では固定せず呼び出し側が明示する。
/// </summary>
public sealed class YaguraCertificateSeverityPolicy
{
    /// <summary>期限切れ証明書のバッジ・注記の重大度。</summary>
    public required YaguraCertificateIssueSeverity ExpiredSeverity { get; init; }

    /// <summary>期限切れ証明書のバッジ文言。</summary>
    public required string ExpiredBadgeText { get; init; }

    /// <summary>期限切れ証明書の候補直下に出す注記の本文。</summary>
    public required string ExpiredAlertText { get; init; }

    /// <summary>
    /// 注記の 2 行目（任意。null なら 1 行のみ表示する）。付与手順への誘導など、画面によって
    /// 有無が分かれる部分をここへ分離する。
    /// </summary>
    public string? ExpiredAlertSecondaryText { get; init; }

    /// <summary>秘密鍵が読めない証明書のバッジ・注記の重大度。</summary>
    public required YaguraCertificateIssueSeverity KeyUnreadableSeverity { get; init; }

    /// <summary>秘密鍵が読めない証明書のバッジ文言。</summary>
    public required string KeyUnreadableBadgeText { get; init; }

    /// <summary>秘密鍵が読めない証明書の候補直下に出す注記の本文。</summary>
    public required string KeyUnreadableAlertText { get; init; }

    /// <summary>注記の 2 行目（任意。null なら 1 行のみ表示する）。</summary>
    public string? KeyUnreadableAlertSecondaryText { get; init; }

    /// <summary>期限切れでも秘密鍵読取不可でもない証明書のバッジ文言。</summary>
    public required string OkBadgeText { get; init; }
}
