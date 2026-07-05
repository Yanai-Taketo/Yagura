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
}
