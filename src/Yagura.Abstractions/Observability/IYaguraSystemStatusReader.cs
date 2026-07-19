namespace Yagura.Abstractions.Observability;

/// <summary>
/// 閲覧画面（M8-3。Issue #70）がホスト管轄の観測値（カウンタ累積値・スプール状態・
/// 判定済みの健康状態・保持期間の適用値・受信リスナの構成）を読むための読み取り専用契約。
/// </summary>
/// <remarks>
/// <para>
/// <b>読み取り専用である</b>——本契約は <c>Yagura.Abstractions.Administration.IYaguraWriteService</c>
/// を実装しない（閲覧リスナ側コンポーネントからの参照が L-5 の参照分離検査
/// ——ViewerComponentReferenceIsolationTests——で許容される側）。サーバ状態を変更する
/// メンバーを本契約へ追加してはならない。
/// </para>
/// <para>
/// <b>配置</b>: 計測の実体（<c>Yagura.Ingestion.Diagnostics.IngestionMetrics</c>）と
/// スプール（<c>Yagura.Storage.Spool.DiskSpool</c>）はホスト側の結線でしか束ねられず、
/// UI（Yagura.Web）は Yagura.Ingestion を参照しない参照構造（architecture.md §1.1）のため、
/// 契約を最下層（本プロジェクト）に置き、実装はホスト
/// （<c>Yagura.Host.Observability.SystemStatusReader</c>）が担う。
/// </para>
/// <para>
/// <b>健康状態の判定は暫定実装</b>（ui.md §5.1・§12 UI-6）: 観測窓の幅・復帰条件は
/// 実利用で確定するまでの仮値であり、実装側（ホスト）が保持する。本契約は判定結果
/// （<see cref="YaguraHealthReading"/>）のみを公開する——部品（YaguraStatusBand）は表示のみを
/// 担う、という ui.md §5.1 の分担のとおり。
/// </para>
/// </remarks>
public interface IYaguraSystemStatusReader
{
    /// <summary>現在の観測値のスナップショットを返す（同期・軽量。DB へはアクセスしない）。</summary>
    YaguraSystemStatusSnapshot ReadCurrent();

    /// <summary>
    /// 流量制限の発火上位送信元を拒否数の多い順に返す（Issue #288。同期・軽量。DB へは
    /// アクセスしない。最大 <paramref name="maxCount"/> 件——実装は上限を安全側に丸めてよい）。
    /// 拒否カウントは流量制御ゲートの有界バケットに併せて保持される値であり、
    /// <b>サービス起動からの累計ではない</b>——制限なく受信できる状態が続いた送信元は
    /// スイープで一覧から消え、流量制御の設定変更でもリセットされる（総数は計器
    /// <c>yagura.ingestion.flow_control.dropped</c> が持つ）。流量制御が無効の構成では常に空。
    /// </summary>
    IReadOnlyList<YaguraFlowControlRejectionReading> ReadFlowControlRejections(int maxCount);

    /// <summary>
    /// 送信元の途絶検知（ADR-0018。Issue #351）のウォッチリスト登録状況と途絶状態を返す
    /// （同期・軽量。DB へはアクセスしない）。ダッシュボードの送信元別受信状況（UI-4）が
    /// 登録済みマーク・途絶中の強調表示に使う。機能無効（ウォッチリスト未設定）の構成では常に空。
    /// アドレスは正規化済み（IPv4-mapped IPv6 は IPv4 表記）——照合側も同じ正規化を通すこと。
    /// </summary>
    IReadOnlyList<YaguraSourceSilenceReading> ReadSourceSilenceEntries();
}

/// <summary>
/// ウォッチリスト 1 エントリの現在状態（ADR-0018 決定 4。UI-4 の表示単位）。
/// </summary>
/// <param name="Address">正規化済みの送信元アドレス。</param>
/// <param name="Label">表示名（未設定なら <see langword="null"/>）。</param>
/// <param name="Threshold">実効閾値。</param>
/// <param name="IsSilent">現在途絶中と判定されているか（判定は周期評価——最大 1 分の遅延を持つ）。</param>
public sealed record YaguraSourceSilenceReading(
    string Address,
    string? Label,
    TimeSpan Threshold,
    bool IsSilent);

/// <summary>流量制限の発火上位送信元の 1 行（Issue #288。ダッシュボードのカード表示単位）。</summary>
/// <param name="SourceAddress">
/// 送信元アドレスの表示文字列（IPv4-mapped IPv6 は純粋な IPv4 表記へ正規化済み——
/// configuration.md §4.1 の送信元アドレス表現の正規化と同じ規約）。
/// </param>
/// <param name="RejectedCount">拒否（破棄）件数（バケット生成からの累計）。</param>
public sealed record YaguraFlowControlRejectionReading(string SourceAddress, long RejectedCount);

/// <summary>
/// <see cref="IYaguraSystemStatusReader.ReadCurrent"/> が返す観測値のスナップショット。
/// </summary>
/// <param name="TakenAt">スナップショット取得時刻（UTC）。</param>
/// <param name="Counters">
/// カウンタ累積値の一覧。<see cref="YaguraCounterReading.InstrumentName"/> は
/// architecture.md §4.1.1 の計器名（<c>yagura.ingestion.*</c>）と 1 対 1 対応する
/// （画面の平易語への対応は ui.md §7 の用語対応表——表示側（UiText）の管轄）。
/// </param>
/// <param name="Spool">スプールの現在状態。スプール無効・縮退運転中は <c>null</c>。</param>
/// <param name="SpoolDegraded">
/// スプールなし縮退運転中か（architecture.md §1.2——スプールを開けず受信のみ続行している状態。
/// 利用者が明示的に無効化した場合は <c>false</c>——縮退ではなく意図した構成）。
/// </param>
/// <param name="Health">健康状態の判定結果（ui.md §5.1 の 3 状態 + 判定理由）。</param>
/// <param name="RetentionDays">
/// 保持期間の適用値（日数。database.md §3）。不正値フォールバック（削除しない）の場合は <c>null</c>。
/// </param>
/// <param name="Listeners">受信リスナの構成（プロトコル・ポート。空状態画面の受信先案内に使う）。</param>
public sealed record YaguraSystemStatusSnapshot(
    DateTimeOffset TakenAt,
    IReadOnlyList<YaguraCounterReading> Counters,
    YaguraSpoolReading? Spool,
    bool SpoolDegraded,
    YaguraHealthReading Health,
    int? RetentionDays,
    IReadOnlyList<YaguraListenerEndpoint> Listeners);

/// <summary>カウンタ累積値の 1 行（計器名 = 開発用語側のキーと、その累積値）。</summary>
/// <param name="InstrumentName">計器名（architecture.md §4.1.1。例: <c>yagura.ingestion.spool.evacuated</c>）。</param>
/// <param name="Value">累積値（前回までの累積 + 今回プロセス分。architecture.md §4.3）。</param>
/// <param name="IsLoss">
/// この計上が「取りこぼし」（サーバに届いた後に失われたログ。ui.md §7.2）を意味するか。
/// 状態帯の異常判定（観測窓内の破棄カウンタの増分——ui.md §5.1）の入力。
/// </param>
public sealed record YaguraCounterReading(string InstrumentName, long Value, bool IsLoss);

/// <summary>スプールの現在状態（architecture.md §4.6 のゲージのうち使用量）。</summary>
/// <param name="CurrentUsageBytes">現在のディスク使用量（バイト）。</param>
/// <param name="QuotaBytes">ディスク使用量上限（バイト）。</param>
public sealed record YaguraSpoolReading(long CurrentUsageBytes, long QuotaBytes)
{
    /// <summary>使用率（0〜1。上限 0 以下なら 0）。</summary>
    public double UsageRatio => QuotaBytes <= 0 ? 0 : (double)CurrentUsageBytes / QuotaBytes;
}

/// <summary>健康状態の判定結果（ui.md §5.1 状態帯の入力）。</summary>
/// <param name="Kind">3 状態（稼働中 / 警告あり / 異常あり）。</param>
/// <param name="Reasons">判定理由（表示側が平易語のサマリへ写像する）。正常時は空。</param>
public sealed record YaguraHealthReading(YaguraHealthKind Kind, IReadOnlyList<YaguraHealthReason> Reasons)
{
    /// <summary>正常（理由なし）。</summary>
    public static YaguraHealthReading Ok { get; } = new(YaguraHealthKind.Ok, []);
}

/// <summary>状態帯の 3 状態（ui.md §5.1）。</summary>
public enum YaguraHealthKind
{
    /// <summary>稼働中（state-ok）。</summary>
    Ok,

    /// <summary>警告あり（state-warning）。</summary>
    Warning,

    /// <summary>異常あり（state-error）。</summary>
    Error,
}

/// <summary>健康状態の判定理由（ui.md §5.1 の判定入力に対応）。</summary>
public enum YaguraHealthReason
{
    /// <summary>観測窓内に取りこぼし（いずれかの破棄カウンタの増分）が発生した（→ 異常あり）。</summary>
    LossObserved,

    /// <summary>
    /// 現在、未消化のスプールデータが残っている（→ 警告あり）。判定は観測窓の増分ではなく
    /// スプールの現在ゲージ（<c>DiskSpool.CurrentUsageBytes &gt; 0</c>）で行う——退避したデータの
    /// DB 格納がすべて完了する（＝スプール使用量が 0 に戻る）と直ちに解除される「消化完了」に
    /// よる復帰（Issue #132。実装: <c>Yagura.Host.Observability.SystemStatusReader</c>）。
    /// </summary>
    SpoolEvacuationObserved,

    /// <summary>スプール使用量が上限に接近している（→ 警告あり）。</summary>
    SpoolUsageNearLimit,

    /// <summary>スプールなし縮退運転中（architecture.md §1.2。→ 警告あり）。</summary>
    SpoolDegraded,
}

/// <summary>受信リスナの構成の 1 行（空状態画面の受信先案内・状態画面の表示に使う）。</summary>
/// <param name="ProtocolName">プロトコル表示名（例: <c>UDP</c> / <c>TCP</c>）。</param>
/// <param name="Port">待ち受けポート番号。</param>
public sealed record YaguraListenerEndpoint(string ProtocolName, int Port);
