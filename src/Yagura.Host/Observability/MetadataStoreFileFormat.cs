namespace Yagura.Host.Observability;

/// <summary>
/// メタデータ領域ファイル（既定 <c>observability-state.json</c>）の JSON 構造にそのまま
/// バインドする POCO（<see cref="Yagura.Host.Configuration.YaguraConfigurationOptions"/> と
/// 同じ「ファイルの生の値を緩い型で受ける」方針。<see cref="MetadataStore"/> がこれと
/// <see cref="MetadataState"/>（検証済みドメイン表現）の間を変換する）。
/// </summary>
/// <remarks>
/// 数値は <c>long?</c> のまま受ける——破損 JSON で型不一致が起きても
/// <see cref="System.Text.Json.JsonSerializer.Deserialize{TValue}(string, System.Text.Json.JsonSerializerOptions?)"/>
/// 自体を例外にせず、後段（<see cref="MetadataStore.Read"/>）で「壊れていれば既定値へ
/// フォールバックする」判断に一元化するため。
/// </remarks>
internal sealed class MetadataStoreFileFormat
{
    /// <summary>ファイル形式のバージョン（将来のスキーマ変更検出用。現状は常に 1）。</summary>
    public int Version { get; set; } = 1;

    public CountersFileFormat? Counters { get; set; }

    public StopEventFileFormat? LastStopEvent { get; set; }

    public string? LastLivenessAt { get; set; }

    internal sealed class CountersFileFormat
    {
        public long? InternalBufferDropped { get; set; }

        public long? TcpConnectionRejected { get; set; }

        public long? SpoolEvacuated { get; set; }

        public long? SpoolWriteFailed { get; set; }

        public long? SpoolDiscarded { get; set; }

        public long? PersistenceFailed { get; set; }

        public long? FlowControlDropped { get; set; }

        // Issue #143・#140 で追加。旧バージョンのファイルにはキーが無いため、読み込み側
        // （MetadataStore.FromFileFormat）は null を 0 として扱う（追加はスキーマの
        // additive-only 原則どおり）。
        public long? TcpConnectionClosed { get; set; }

        public long? TcpConnectionIdleTimeout { get; set; }

        public long? TcpMessageOversizedDiscarded { get; set; }

        // PR #169 レビュー指摘 3 へのオーナー決定（2026-07-09）で追加。上と同じく additive-only。
        public long? TcpConnectionResyncLimitExceeded { get; set; }

        public long? TcpConnectionFramingTimeout { get; set; }

        // Issue #201 で追加。上と同じく additive-only（旧バージョンのファイルにはキーが無いため
        // null を 0 として扱う）。単位は他フィールドと異なりバイト（IngestionCounterSnapshot.
        // SpoolCorruptTailDiscardedBytes・IngestionMetrics remarks 参照）。
        public long? SpoolCorruptTailDiscardedBytes { get; set; }
    }

    internal sealed class StopEventFileFormat
    {
        public string? ReceiveSocketClosedAt { get; set; }

        public string? StoppedAt { get; set; }
    }
}
