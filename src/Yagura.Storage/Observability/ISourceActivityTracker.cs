namespace Yagura.Storage.Observability;

/// <summary>
/// 送信元ごとの「最後に受信した時刻」を追跡する（ADR-0018 決定 3）。
/// </summary>
/// <remarks>
/// <para>
/// <b>本契約が <c>Yagura.Storage</c> に居る理由</b>: 書き手は 2 系統——受信段
/// （<c>Yagura.Ingestion</c> の <c>ParsingStage</c> 消費ループ）と、スプール drain の合流点
/// （<c>Yagura.Storage</c>）——であり、両方から参照できる層が要る。
/// <c>Yagura.Ingestion</c> は <c>Yagura.Abstractions</c> を参照していない（受信段は
/// スプール〔<c>Yagura.Storage</c>〕だけを下流に持つ）ため、共通の可視層は <c>Yagura.Storage</c>
/// になる。これは <c>SpoolSelfTestTracker</c> が同じ層に居るのと同じ理由であり、ADR-0018 決定 3 の
/// 「<c>SpoolSelfTestTracker</c> と同じ注入形」はこの配置を指している。実体は Host 側にあり、
/// 監視ループが読む。
/// </para>
/// <para>
/// <b>アドレスを文字列で受ける</b>のは意図的である。呼び出し元（<c>RawDatagram.SourceAddress</c>・
/// スプールのレコード）はいずれも文字列を持っており、<b>受信ホットパスで <c>IPAddress</c> へ
/// 解析させると 1 データグラムごとに解析とアロケーションが発生する</b>。IPv4-mapped IPv6 の
/// 正規化はウォッチリスト適用時（実装側）に寄せ、ホットパスは辞書引き 1 回で済ませる。
/// </para>
/// <para>
/// 実装は<b>ブロックせず・例外を漏らさず・未登録の送信元では何も確保しない</b>こと
/// （ADR-0018 の受け入れ基準「未登録送信元の受信がインメモリ追跡に一切影響しない」——
/// 攻撃者が送信元アドレスを変えながら送るだけでメモリを食い潰せる作りにしない）。
/// </para>
/// </remarks>
public interface ISourceActivityTracker
{
    /// <summary>
    /// 当該送信元からの受信を記録する。ウォッチリスト外のアドレスは<b>何もしない</b>。
    /// </summary>
    /// <param name="sourceAddress">送信元アドレスの文字列表現。</param>
    void RecordActivity(string sourceAddress);

    /// <summary>
    /// <b>過去の</b>受信実績を遅延反映する（スプール drain の合流点から呼ぶ）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// これがある理由（ADR-0018 決定 3）: <b>深いスプール滞留 + 再起動</b>の組で、直前まで
    /// 送っていた装置が途絶に見える偽陽性を塞ぐため。architecture.md §3.2.2 は滞留を正常状態と
    /// 明記しており「短い窓」ではない——滞留中のレコードが後から DB へ合流するとき、その
    /// レコードが実際に受信された時刻を追跡へ反映しないと、装置は健在なのに「閾値ぶん受信が
    /// ない」と判定され得る。
    /// </para>
    /// <para>
    /// <b>これは新規受信の定義ではない</b>（決定 3 の「受信した」の定義の唯一の例外）。実装は
    /// <c>max()</c> 更新であり、より新しい実受信を過去へ引き戻してはならない。
    /// </para>
    /// </remarks>
    /// <param name="sourceAddress">送信元アドレスの文字列表現。</param>
    /// <param name="observedAt">当該レコードが実際に受信された壁時計時刻。</param>
    void RecordHistoricalActivity(string sourceAddress, DateTimeOffset observedAt);
}
