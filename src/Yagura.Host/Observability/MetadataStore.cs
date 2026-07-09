using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Diagnostics;

namespace Yagura.Host.Observability;

/// <summary>
/// メタデータ領域（architecture.md §4.3）の読み書きを担う。カウンタ累積値・停止イベント・
/// 生存時刻を保存する<b>ホスト管轄の独立ローカルファイル</b>。DB provider には依存しない
/// （§4.3「DB 障害中・provider 切替中にこそ記録が必要になるため、DB 内には置かない」）。
/// </summary>
/// <remarks>
/// <para>
/// <b>形式: JSON の全体書き換え + 原子的置換</b>。設定ファイル
/// （<see cref="Yagura.Host.Configuration.YaguraConfigurationWriter"/>）と同じ方式を採用した。
/// 判断理由: (i) メタデータ領域は「一定間隔 + 停止時」の低頻度書き込みであり、スプール
/// （高頻度追記に最適化した専用セグメント形式）ほどの書き込み性能は要求されない、
/// (ii) 内容（カウンタ 12 個・停止イベント・生存時刻）は数十バイト程度で全体書き換えの
/// コストが無視できる、(iii) 本リポジトリに既に「一時ファイル + <see cref="File.Replace(string, string, string?)"/>
/// （初回は <see cref="File.Move(string, string, bool)"/>）」という原子的置換の実装・検証済み
/// パターンが存在し、同じ判断枠組みを流用できる。**追記型のスプール形式を新設する理由はない**
/// と判断した。
/// </para>
/// <para>
/// <b>破損時のフォールバック</b>: 強制終了により一時ファイルへの書き込み途中で終わった場合でも、
/// <see cref="File.Replace(string, string, string?)"/>／<see cref="File.Move(string, string, bool)"/>
/// は「対象ファイルへ壊れた内容が見える瞬間を作らない」（YaguraConfigurationWriter と同じ
/// 原子性の根拠）。したがって<b>対象ファイル自体が破損する経路は原理的にない</b>——読み込み時に
/// 検出し得る破損は「対象ファイルが存在するが JSON として不正」（手編集・ディスク破損等の
/// 別要因）のみであり、<see cref="Read"/> はこの場合に例外を投げず <see cref="MetadataState.Initial"/>
/// を返す（ゼロから再開 + 警告は呼び出し側がログに出す）。
/// </para>
/// <para>
/// <b>文字コード</b>: UTF-8 BOM なし（<see cref="YaguraConfigurationWriter"/> と同じ理由——
/// 手編集ツールとの親和性。本ファイルは手編集を想定しないが、方式を統一する）。
/// </para>
/// </remarks>
public static class MetadataStore
{
    /// <summary>データルート配下のメタデータ領域ファイル名。</summary>
    public const string FileName = "observability-state.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// メタデータ領域ファイルのパスを返す。
    /// </summary>
    public static string GetFilePath(string dataRoot) => Path.Combine(dataRoot, FileName);

    /// <summary>
    /// メタデータ領域を読み込む。ファイルが存在しない場合（初回起動）は
    /// <see cref="MetadataState.Initial"/> を返す。ファイルが存在するが破損している場合
    /// （JSON として不正・型不一致）も例外を投げず <see cref="MetadataState.Initial"/> を返し、
    /// <paramref name="logger"/> へ警告を出す（「ゼロから再開 + 警告」。§4.3 の実装ノート）。
    /// </summary>
    public static MetadataState Read(string dataRoot, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var path = GetFilePath(dataRoot);

        if (!File.Exists(path))
        {
            return MetadataState.Initial;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var fileFormat = JsonSerializer.Deserialize<MetadataStoreFileFormat>(bytes);

            if (fileFormat is null)
            {
                logger?.LogWarning(
                    "[metadata-store-corrupt] メタデータ領域 {Path} の内容が空またはJSONとして解釈できないため、" +
                    "カウンタ・生存時刻をゼロから再開します。",
                    path);
                return MetadataState.Initial;
            }

            return FromFileFormat(fileFormat);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 破損（不正 JSON・型不一致）またはファイル I/O 障害。§4.3「ゼロから再開 + 警告」——
            // 起動そのものは失敗させない（メタデータ領域は受信の成立に不可欠ではない）。
            logger?.LogWarning(
                ex,
                "[metadata-store-corrupt] メタデータ領域 {Path} の読み込みに失敗したため、" +
                "カウンタ・生存時刻をゼロから再開します。",
                path);
            return MetadataState.Initial;
        }
    }

    /// <summary>
    /// メタデータ領域を全体書き換えで保存する（原子的置換。本クラスの remarks 参照）。
    /// </summary>
    public static void Save(string dataRoot, MetadataState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(state);

        Directory.CreateDirectory(dataRoot);
        var path = GetFilePath(dataRoot);

        var fileFormat = ToFileFormat(state);
        var content = JsonSerializer.SerializeToUtf8Bytes(fileFormat, SerializerOptions);

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tempPath, content);

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path, overwrite: false);
            }
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private static MetadataState FromFileFormat(MetadataStoreFileFormat fileFormat)
    {
        var counters = fileFormat.Counters is { } c
            ? new IngestionCounterSnapshot(
                InternalBufferDropped: c.InternalBufferDropped ?? 0,
                TcpConnectionRejected: c.TcpConnectionRejected ?? 0,
                SpoolEvacuated: c.SpoolEvacuated ?? 0,
                SpoolWriteFailed: c.SpoolWriteFailed ?? 0,
                SpoolDiscarded: c.SpoolDiscarded ?? 0,
                PersistenceFailed: c.PersistenceFailed ?? 0,
                FlowControlDropped: c.FlowControlDropped ?? 0,
                TcpConnectionClosed: c.TcpConnectionClosed ?? 0,
                TcpConnectionIdleTimeout: c.TcpConnectionIdleTimeout ?? 0,
                TcpMessageOversizedDiscarded: c.TcpMessageOversizedDiscarded ?? 0,
                TcpConnectionResyncLimitExceeded: c.TcpConnectionResyncLimitExceeded ?? 0,
                TcpConnectionFramingTimeout: c.TcpConnectionFramingTimeout ?? 0)
            : IngestionCounterSnapshot.Zero;

        StopEventRecord? stopEvent = null;
        if (fileFormat.LastStopEvent is { ReceiveSocketClosedAt: { } closedAtText, StoppedAt: { } stoppedAtText }
            && TryParseUtc(closedAtText, out var closedAt)
            && TryParseUtc(stoppedAtText, out var stoppedAt))
        {
            stopEvent = new StopEventRecord(closedAt, stoppedAt);
        }

        DateTimeOffset? lastLivenessAt = fileFormat.LastLivenessAt is { } livenessText && TryParseUtc(livenessText, out var liveness)
            ? liveness
            : null;

        return new MetadataState(counters, stopEvent, lastLivenessAt);
    }

    private static MetadataStoreFileFormat ToFileFormat(MetadataState state) => new()
    {
        Version = 1,
        Counters = new MetadataStoreFileFormat.CountersFileFormat
        {
            InternalBufferDropped = state.Counters.InternalBufferDropped,
            TcpConnectionRejected = state.Counters.TcpConnectionRejected,
            SpoolEvacuated = state.Counters.SpoolEvacuated,
            SpoolWriteFailed = state.Counters.SpoolWriteFailed,
            SpoolDiscarded = state.Counters.SpoolDiscarded,
            PersistenceFailed = state.Counters.PersistenceFailed,
            FlowControlDropped = state.Counters.FlowControlDropped,
            TcpConnectionClosed = state.Counters.TcpConnectionClosed,
            TcpConnectionIdleTimeout = state.Counters.TcpConnectionIdleTimeout,
            TcpMessageOversizedDiscarded = state.Counters.TcpMessageOversizedDiscarded,
            TcpConnectionResyncLimitExceeded = state.Counters.TcpConnectionResyncLimitExceeded,
            TcpConnectionFramingTimeout = state.Counters.TcpConnectionFramingTimeout,
        },
        LastStopEvent = state.LastStopEvent is { } stopEvent
            ? new MetadataStoreFileFormat.StopEventFileFormat
            {
                ReceiveSocketClosedAt = stopEvent.ReceiveSocketClosedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                StoppedAt = stopEvent.StoppedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            }
            : null,
        LastLivenessAt = state.LastLivenessAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
    };

    private static bool TryParseUtc(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
}
