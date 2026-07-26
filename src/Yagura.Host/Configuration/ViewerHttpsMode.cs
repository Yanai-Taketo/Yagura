namespace Yagura.Host.Configuration;

/// <summary>
/// 閲覧 UI の HTTPS（ADR-0022）の解決済み動作モード。設定の静的検証
/// （<see cref="YaguraConfigurationLoader"/>）の結果であり、証明書ストア参照の成否
/// （環境依存——<c>Program</c> 側で確認する）は含まない。
/// </summary>
public enum ViewerHttpsMode
{
    /// <summary>
    /// HTTPS 無効（既定）。閲覧リスナは従来どおり平文 HTTP で listen する。
    /// </summary>
    Disabled,

    /// <summary>
    /// HTTPS 有効（<c>Viewer:Https:Enabled = true</c> + 形式上有効な証明書拇印）。
    /// 閲覧リスナは同一ポート（<c>Viewer:HttpPort</c>）のまま HTTPS で listen する
    /// （ADR-0022 決定 1）。実際に開けるかどうかは証明書ストア参照の成否
    /// （<c>Program</c>）に依存し、解決できなければ
    /// <see cref="SuppressListener"/> と同じ縮小継続になる（ADR-0022 決定 2）。
    /// </summary>
    Enabled,

    /// <summary>
    /// 閲覧リスナを開かない縮小継続（ADR-0022 決定 1・2）。利用者が HTTPS を意図した
    /// 証跡（拇印の設定）があるのに HTTPS を成立させられない設定——
    /// (a) <c>Enabled = true</c> だが拇印が未設定・形式不正、
    /// (b) <c>Enabled</c> が真偽値として不正だが拇印が設定済み——であり、
    /// 平文（無効）へ倒すと暗号化の意図が黙って外れるため、閲覧リスナを開かない側へ倒す
    /// （<c>Notification:Email:Smtp:Security</c> の前例に従う。受信・解析・保存・スプール・
    /// 管理リスナは影響を受けず、サーバ上の閲覧・復旧は管理リスナ（loopback）経由で可能）。
    /// </summary>
    SuppressListener,
}
