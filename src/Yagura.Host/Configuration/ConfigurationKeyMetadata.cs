namespace Yagura.Host.Configuration;

/// <summary>
/// 設定キーごとの反映方式（configuration.md §3）の宣言表。
/// </summary>
/// <remarks>
/// <para>
/// <b>属性ではなく静的表を選んだ理由</b>: 反映方式の判定対象は
/// <see cref="YaguraConfigurationOptions"/> の CLR プロパティではなく、
/// <see cref="YaguraConfigurationLoader"/> が既に「JSON キーパス（<c>:</c> 区切り文字列）」
/// という単位で管理している（<c>KnownKeys</c>・<see cref="ConfigurationWarning.Key"/> 等）。
/// C# 属性（<c>[ReloadEffect(...)]</c>）で宣言する場合、POCO のプロパティに付与したうえで
/// リフレクションによりプロパティ階層からキーパス文字列を組み立て直す必要があり、
/// ネストされた <c>Options</c> クラス（<see cref="YaguraConfigurationOptions.IngestionOptions.UdpOptions"/>
/// 等）では入れ子名とキーパスの対応規則を別途定義しなければならない。
/// 一方、静的表はキーパス文字列をそのままキーにでき、<c>KnownKeys</c> と同じ文字列を
/// 追加するだけで済む。**新しい設定キーを追加する PR は、本表と <c>KnownKeys</c> と
/// configuration.md §8 の 3 箇所を同時に更新すること**（configuration.md §8「設定キー追加の規約」）。
/// </para>
/// <para>
/// <b>既存 4 キーの割当</b>（依頼時点の解釈どおり。configuration.md §3・§8 に別段の記載がないため
/// 採用）:
/// <list type="bullet">
/// <item><c>Ingestion:Udp:BindAddress</c> / <c>Ingestion:Udp:Port</c> = リスナ再構成
/// （§8 表「受信」区分の目標値。無瞬断で適用できる項目の特定は CF-4 で行うため、
/// 実装がまだ存在しない現時点でも宣言は設計目標どおり「リスナ再構成」とする。
/// 実際にリスナを再構成する処理は本 Issue の範囲外——実装しない——のため、
/// 現状ではこの宣言が計算されても実際に反映されるのは次回起動時のみである）。</item>
/// <item><c>Viewer:HttpPort</c> = サービス再起動（§8 表「UI」区分は「リスナ再構成」を
/// 目標に掲げるが、Kestrel の listen アドレスは <c>WebApplication</c> 構築時に固定され
/// 実行中の付け替え API を持たないため、無瞬断のリスナ再構成が実装されるまでの間は
/// 再起動が実効的な反映方式である。CF-4 確定後に見直す）。</item>
/// <item><c>Storage:SqliteFileName</c> = サービス再起動（§8 表「永続化」区分「組み込み DB
/// の置き場所」は明示的に「置き場所はサービス再起動」と記載）。</item>
/// <item><c>Spool:Enabled</c> / <c>Spool:QuotaBytes</c> = 即時（§8 表「スプール」区分
/// 「有効/無効（opt-out）・置き場所・上限（M-12）| 即時（置き場所のみサービス再起動）」の
/// とおり）。<c>Spool:Directory</c> = サービス再起動（同表の「置き場所のみ」の例外。M4-3）。</item>
/// </list>
/// </para>
/// </remarks>
public static class ConfigurationKeyMetadata
{
    private static readonly IReadOnlyDictionary<string, ConfigurationReloadEffect> ReloadEffectsByKey =
        new Dictionary<string, ConfigurationReloadEffect>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ingestion:Udp:BindAddress"] = ConfigurationReloadEffect.ListenerReconfiguration,
            ["Ingestion:Udp:Port"] = ConfigurationReloadEffect.ListenerReconfiguration,
            // TCP キー(M4-1)は UDP と同じ「受信」区分(§8)。本表への登録が M4-1 で漏れており
            // M5-1 のレビューで追補した(KnownKeys との整合はテストで機械検証される)。
            ["Ingestion:Tcp:BindAddress"] = ConfigurationReloadEffect.ListenerReconfiguration,
            ["Ingestion:Tcp:Port"] = ConfigurationReloadEffect.ListenerReconfiguration,
            ["Viewer:HttpPort"] = ConfigurationReloadEffect.RestartRequired,
            ["Storage:SqliteFileName"] = ConfigurationReloadEffect.RestartRequired,
            // provider 切替は database.md §6.1 の切替手順（準備フェーズ→切替本番）による専用の
            // 管理操作であり、通常の設定再読み込み（差分適用）の対象ではない。現時点は
            // ウィザード未実装のため実効はサービス再起動（configuration.md §8 表「永続化」区分
            // 「provider 切替は database.md §6.1 の切替手順による（備考: ウィザード経由のみ）」）。
            ["Storage:Provider"] = ConfigurationReloadEffect.RestartRequired,
            ["Storage:SqlServer:ConnectionString"] = ConfigurationReloadEffect.RestartRequired,
            ["Spool:Enabled"] = ConfigurationReloadEffect.Immediate,
            ["Spool:Directory"] = ConfigurationReloadEffect.RestartRequired,
            ["Spool:QuotaBytes"] = ConfigurationReloadEffect.Immediate,
            // 保持期間(M5-1)は §8 表「保持期間 | 即時」の目標どおり宣言する(Spool:Enabled と
            // 同じ扱い。現時点の実効はスケジューラが起動時にのみ設定を読むため再起動だが、
            // ライブ再読込(§3)配線時に目標へ揃える)。
            ["Retention:Days"] = ConfigurationReloadEffect.Immediate,
            ["Retention:ExecutionTimeOfDay"] = ConfigurationReloadEffect.Immediate,
        };

    /// <summary>
    /// 指定したキーの反映方式を返す。未登録のキーは呼び出し側の設定漏れ（新キー追加時に
    /// 本表の更新を忘れた等）であるため、既定値へ黙ってフォールバックせず例外にする。
    /// </summary>
    /// <exception cref="KeyNotFoundException">キーが本表に未登録の場合。</exception>
    public static ConfigurationReloadEffect GetReloadEffect(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (ReloadEffectsByKey.TryGetValue(key, out var effect))
        {
            return effect;
        }

        throw new KeyNotFoundException(
            $"設定キー '{key}' の反映方式が ConfigurationKeyMetadata に未登録です。" +
            "新しい設定キーを追加する場合は、本表と YaguraConfigurationLoader の KnownKeys と " +
            "configuration.md §8 を同じ PR で更新してください（configuration.md §8「設定キー追加の規約」）。");
    }

    /// <summary>本表に登録済みのすべてのキー。テスト・網羅性検証用。</summary>
    public static IReadOnlyCollection<string> RegisteredKeys => (IReadOnlyCollection<string>)ReloadEffectsByKey.Keys;
}
