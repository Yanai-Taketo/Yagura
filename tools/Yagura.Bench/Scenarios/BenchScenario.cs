namespace Yagura.Bench.Scenarios;

/// <summary>
/// Issue #60 記載の 5 シナリオ（architecture.md §5.1「計測対象の最小セット」）。
/// </summary>
public enum BenchScenario
{
    /// <summary>受信スループット（UDP/TCP 別）。</summary>
    Throughput,

    /// <summary>破棄ゼロで維持できる持続流量。</summary>
    SustainedZeroDrop,

    /// <summary>バースト負荷時の Q1 破棄の発生有無（§3.1 の前提検証）。</summary>
    BurstQ1Drop,

    /// <summary>スプール発動 → 追いつきの所要。</summary>
    SpoolActivationRecovery,

    /// <summary>SQLite / SQL Server 各 provider の書き込み上限。</summary>
    ProviderWriteCeiling,

    /// <summary>
    /// 対話的検索のクエリレイテンシ（DB-9。database.md §4・§8）。SQLite の自由文検索を
    /// ネイティブ ASCII 限定 <c>LIKE</c> と、アプリ定義比較関数（<c>SqliteConnection.CreateFunction</c>）
    /// 候補案の間で行数規模別に比較する。受信パイプラインを経由せず、<see cref="Yagura.Storage.ILogStore"/>
    /// への直接書き込み・直接クエリで計測する（このシナリオは Throughput 系 5 シナリオと異なり
    /// 受信パイプラインの負荷測定ではなく、ストレージ層のクエリ性能そのものが対象のため）。
    /// </summary>
    QueryLatency,

    /// <summary>
    /// スキーマ v1→v2 移行（COLLATE 明示等）の DDL 実行時間（DB-10。database.md §5.4・§8）。
    /// v1 形状で行数規模別にデータを投入した DB に対して <c>InitializeAsync</c>
    /// （移行の実体）を実行し、所要時間・SQL Server ではロック挙動（移行中の書き込み可否）を計測する。
    /// <c>--sqlserver</c> 指定時は SQL Server（LocalDB 既定）、未指定時は SQLite を対象にする。
    /// </summary>
    SchemaMigrationDdl,
}
