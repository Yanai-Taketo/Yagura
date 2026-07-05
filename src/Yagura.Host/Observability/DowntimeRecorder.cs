using Yagura.Storage;

namespace Yagura.Host.Observability;

/// <summary>
/// 起動時に前回終了までの記録（<see cref="MetadataState"/>）から受信断区間を確定する
/// （architecture.md §4.4）。
/// </summary>
/// <remarks>
/// 「区間の確定は起動時に行う」（§4.4）——本クラスは起動シーケンス中に 1 回だけ呼ばれる想定で、
/// 受信断の有無・種別（正常停止 / クラッシュ近似）を判定し、書き込むべき <see cref="SystemEvent"/>
/// を返す（実際の書き込みは呼び出し側 <see cref="Yagura.Host.IngestionHostedService"/> が
/// <see cref="ILogStore.WriteSystemEventAsync"/> 経由で行う）。
/// </remarks>
public static class DowntimeRecorder
{
    /// <summary>
    /// 前回終了時の記録と今回の受信開始時刻から、記録すべき受信断区間（あれば）を判定する。
    /// </summary>
    /// <param name="previousState">前回終了時までに読み込んだメタデータ領域の状態。</param>
    /// <param name="receiveStartedAt">今回の受信開始時刻（UTC。architecture.md §1.2 手順 2）。</param>
    /// <returns>
    /// 記録すべき <see cref="SystemEvent"/>。初回起動（<see cref="MetadataState.LastStopEvent"/>
    /// も <see cref="MetadataState.LastLivenessAt"/> も無い場合）は受信断の起点が無いため
    /// <c>null</c> を返す——「前回」が存在しない以上、区間を構成する情報自体がない。
    /// </returns>
    public static SystemEvent? DetermineDowntimeEvent(MetadataState previousState, DateTimeOffset receiveStartedAt)
    {
        ArgumentNullException.ThrowIfNull(previousState);

        if (previousState.LastStopEvent is { } stopEvent)
        {
            // 正常停止: 「停止〜起動」を受信断区間として保存する（§4.4「正常停止」）。
            // 区間の開始は手順 1「受信ソケットを閉じ」た時刻（ReceiveSocketClosedAt）——
            // 停止処理全体の完了時刻（StoppedAt）ではなく、実際に受信が止まった瞬間を使う。
            return new SystemEvent(
                Kind: ObservabilityConstants.SystemEventKindDowntimeNormalStop,
                StartAt: stopEvent.ReceiveSocketClosedAt,
                EndAt: receiveStartedAt,
                Approximate: false);
        }

        if (previousState.LastLivenessAt is { } lastLivenessAt)
        {
            // クラッシュ近似断点: 前回の正常停止イベントが無く、生存時刻だけが残っている場合
            // （§4.4「前回が正常停止でない場合…最終生存時刻を近似の断点として受信断区間を
            // 保存する」）。
            return new SystemEvent(
                Kind: ObservabilityConstants.SystemEventKindDowntimeCrashApproximate,
                StartAt: lastLivenessAt,
                EndAt: receiveStartedAt,
                Approximate: true);
        }

        // 前回の記録が一切ない（メタデータ領域が初回作成 = このプロセスがこのデータルートでの
        // 最初の起動）——受信断区間を構成する起点がないため、記録しない。
        return null;
    }
}
