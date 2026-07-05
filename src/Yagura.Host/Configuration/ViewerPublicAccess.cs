namespace Yagura.Host.Configuration;

/// <summary>
/// 閲覧リスナの公開範囲の選択（設定キー <c>Viewer:PublicAccess</c>。M6-1。configuration.md §4.2）。
/// </summary>
/// <remarks>
/// 既定は <see cref="Lan"/>（ADR-0004 決定 2・configuration.md §4.2「閲覧リスナは既定で LAN に
/// 公開する」）。不正値は configuration.md §1「縮小側で継続」により <see cref="LocalhostOnly"/>
/// （より狭い側）へ縮小する——製品既定（開放側）へ落とさない設計原則の適用。
/// </remarks>
public enum ViewerPublicAccess
{
    /// <summary>LAN へ公開する（既定。全インターフェースへ bind）。</summary>
    Lan,

    /// <summary>localhost（127.0.0.1 / ::1）のみへ bind する。</summary>
    LocalhostOnly,
}
