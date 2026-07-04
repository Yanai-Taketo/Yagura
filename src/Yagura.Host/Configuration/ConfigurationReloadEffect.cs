namespace Yagura.Host.Configuration;

/// <summary>
/// 設定キー 1 件を変更したときに必要となる反映方式（configuration.md §3「反映方式を項目の
/// 属性として持つ」）。
/// </summary>
/// <remarks>
/// <para>
/// 列挙順序は反映コストの昇順であり、そのまま「必要な反映アクションの最大」の比較に使う
/// （<see cref="ConfigurationChangePlanner"/> 参照）。数値の大小関係に意味を持たせるため、
/// 明示的に 0/1/2 を割り当てる。
/// </para>
/// <para>
/// 3 分類の意味（§3）:
/// <list type="bullet">
/// <item><see cref="Immediate"/>: 即時反映。接続の瞬断を伴わない。</item>
/// <item><see cref="ListenerReconfiguration"/>: リスナ再構成。接続の瞬断を伴う（§3 の
/// 「リスナ再構成の瞬断も観測対象とする」）。実際の無瞬断再構成の実装は CF-4（M4 以降）。</item>
/// <item><see cref="RestartRequired"/>: サービス再起動が必要。受信断を伴う。</item>
/// </list>
/// </para>
/// </remarks>
public enum ConfigurationReloadEffect
{
    /// <summary>即時反映（接続の瞬断を伴わない）。</summary>
    Immediate = 0,

    /// <summary>リスナ再構成（接続の瞬断を伴う）。</summary>
    ListenerReconfiguration = 1,

    /// <summary>サービス再起動が必要（受信断を伴う）。</summary>
    RestartRequired = 2,
}
