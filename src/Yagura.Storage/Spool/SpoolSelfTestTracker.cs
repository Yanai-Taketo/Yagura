namespace Yagura.Storage.Spool;

/// <summary>
/// 定期自己検証（architecture.md §3.2.5。Issue #152）の投入・照合状態を保持する。
/// </summary>
/// <remarks>
/// <para>
/// <b>存在理由</b>: 合成レコードの投入は Yagura.Host 側（周期監視を持つ
/// <c>ActiveNotificationMonitor</c>）が担い、drain の実機構に読ませて照合するのは
/// Yagura.Ingestion 側（<c>SpoolDrainCoordinator</c>）が担う——「drain の常時動作」の一部として
/// 合成レコードを識別・破棄する既存の経路（§3.2.5「この識別・破棄は定期検証時だけでなく drain
/// の常時動作である」）をそのまま検証に使うのが本節の要求であり、専用の検証用書込・読出経路は
/// 設けない。両プロジェクトの橋渡しは、Host からも Ingestion からも一方向に参照される
/// Yagura.Storage 層に置くことで、参照方向（Host → Ingestion、両者 → Storage）を逆転させずに
/// 同一インスタンスを共有できる（<c>DiskSpool</c> 自体が既にこのパターンで両プロジェクトから
/// 参照されているのと同じ設計）。
/// </para>
/// <para>
/// <b>覆域の限界（§3.2.5 に明記のとおり）</b>: 本トラッカーが検証するのは「スプール書込 →
/// セグメント読出 → 逆直列化 → drain 合流判定」までであり、「drain が読んだレコードを実際に
/// DB へコミットする」結合部は対象外（そこは受け入れテストが担う）。
/// </para>
/// <para>
/// <b>未照合は常に高々 1 件</b>: 新しいマーカーを発行すると、直前のマーカーが未照合のままでも
/// 上書きする。検証の目的は「経路が生きているか」の継続的な確認であり、複数マーカーを並行して
/// 追跡する必要はない（タイムアウト判定は呼び出し側 <c>ActiveNotificationMonitor</c> の責務。
/// 新マーカー発行前にタイムアウト判定を済ませる想定）。
/// </para>
/// <para>
/// <b>スレッド安全性</b>: 投入（<see cref="BeginNewMarker"/>・<see cref="IsPendingTimedOut"/>）は
/// <c>ActiveNotificationMonitor</c> の単一の周期監視ループから、照合
/// （<see cref="OnSelfTestRecordDrained"/>）は <c>SpoolDrainCoordinator</c> の単一の drain
/// ループから、それぞれ異なるスレッドで呼ばれ得る。内部状態は <see cref="_gate"/> で保護する。
/// </para>
/// </remarks>
public sealed class SpoolSelfTestTracker
{
    private readonly object _gate = new();
    private string? _pendingMarker;
    private DateTimeOffset _pendingSince;

    /// <summary>
    /// 新しい自己検証マーカーを発行し、未照合状態として記録する。
    /// </summary>
    /// <param name="now">投入時刻（<c>TimeProvider</c> 経由。テストで決定的に制御するため）。</param>
    /// <returns><see cref="Yagura.Storage.Spool.SpoolRecord.ForSelfTest"/> へそのまま渡すマーカー文字列。</returns>
    public string BeginNewMarker(DateTimeOffset now)
    {
        var marker = Guid.NewGuid().ToString("N");

        lock (_gate)
        {
            _pendingMarker = marker;
            _pendingSince = now;
        }

        return marker;
    }

    /// <summary>
    /// drain が自己検証レコードを合流判定した（DB 書き込み直前で破棄した）ことを通知する
    /// （§3.2.5「drain の実機構に読ませて照合する」）。現在未照合のマーカーと一致する場合のみ
    /// 照合済みにする——古い（既にタイムアウト通知済みで上書きされた）マーカーが遅れて drain
    /// された場合は無視する。
    /// </summary>
    public void OnSelfTestRecordDrained(string marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);

        ClearIfMatches(marker);
    }

    /// <summary>
    /// 投入（スプール書込）に失敗したマーカーの未照合登録を取り消す（PR #200 レビュー指摘への
    /// 対応）。ディスクへ書かれなかったマーカーは drain に照合される見込みが無く、未照合のまま
    /// 残すと「即時の書込失敗通知」に加えて「タイムアウト通知」（別トリガキー）が次回投入
    /// （最大 1 日後）まで抑制窓ごとに反復発火するノイズ源になるため、投入側は書込失敗を
    /// 確認した時点で本メソッドを呼び登録を取り消す。現在未照合のマーカーと一致する場合のみ
    /// 取り消す。
    /// </summary>
    /// <remarks>
    /// 「書込成功を確認した後にのみ登録する」順序にしなかった理由: マーカーは書込前にレコードへ
    /// 埋め込む必要があり、書込完了と投入側の後続処理（登録）の間に、別スレッドで常時動作する
    /// drain がそのレコードを読み <see cref="OnSelfTestRecordDrained"/> を呼び得る。未登録の
    /// マーカーへの通知は黙って無視される設計のため、成功後に登録する方式では「照合済みのはずが
    /// 未照合として登録され偽のタイムアウトに至る」競合が生まれる。先に登録し失敗時に取り消す
    /// 本方式は、この競合を原理的に持たない。
    /// </remarks>
    public void CancelPending(string marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);

        ClearIfMatches(marker);
    }

    private void ClearIfMatches(string marker)
    {
        lock (_gate)
        {
            if (string.Equals(_pendingMarker, marker, StringComparison.Ordinal))
            {
                _pendingMarker = null;
            }
        }
    }

    /// <summary>
    /// 現在未照合のマーカーが存在し、かつ投入から <paramref name="timeout"/> 以上経過しているかを判定する。
    /// 未照合マーカーが無い（未投入、または既に照合済み）場合は <c>false</c>。
    /// </summary>
    public bool IsPendingTimedOut(DateTimeOffset now, TimeSpan timeout)
    {
        lock (_gate)
        {
            return _pendingMarker is not null && now - _pendingSince >= timeout;
        }
    }
}
