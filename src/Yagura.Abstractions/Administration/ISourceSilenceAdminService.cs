using Yagura.Abstractions.Observability;

namespace Yagura.Abstractions.Administration;

/// <summary>
/// 送信元の途絶検知（ADR-0018。opt-in・既定無効）のウォッチリスト設定
/// （管理画面 <c>/admin/source-silence</c> の書き込み系サービス。Issue #351）。
/// </summary>
/// <remarks>
/// <para>
/// <b>登録は既知送信元からの候補選択を主経路とする</b>（ADR-0018 決定 4——IP の転記ミスによる
/// 「形式は正しいが違うアドレス」の黙った空振りを構造的に減らす）。手入力は「まだ受信のない
/// 先回り登録」用に残す。
/// </para>
/// <para>
/// <b>拒否と受理の区別</b>: アドレスの形式不正・正規化後の重複・閾値の範囲外
/// （<see cref="SourceSilenceAdminStatus.MinThresholdMinutes"/>〜
/// <see cref="SourceSilenceAdminStatus.MaxThresholdMinutes"/>）・上限超過・
/// <b>新規エントリの閾値未指定</b>（UI 経由の登録は閾値の明示確定を必須とする——決定 4。
/// 「登録した = すぐ気づける」という期待と既定 24 時間のズレを登録動線で解消する）は、いずれも
/// <see cref="WizardValidationException"/> で<b>保存しない</b>。手編集なら
/// <c>YaguraConfigurationLoader</c> がエントリ単位の無効化 + 警告に倒す構成でも、利用者が
/// 目の前にいる保存時はその場で理由を示して拒否するほうが原因に近い（メール通知設定と同じ判断）。
/// 手編集で閾値を省略した<b>既存</b>エントリは、省略のまま保持できる（既定値の補完を尊重する）。
/// </para>
/// </remarks>
public interface ISourceSilenceAdminService : IYaguraWriteService
{
    /// <summary>現在の設定（ファイルの生値）と稼働中の判定状態を取得する。</summary>
    Task<SourceSilenceAdminStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 登録候補（実受信のある送信元）を最終受信時刻の新しい順で返す（ADR-0018 決定 4 の候補選択。
    /// 各候補に最終受信時刻・累計件数を添える）。
    /// </summary>
    Task<IReadOnlyList<SourceSilenceCandidate>> GetCandidatesAsync(
        int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// ウォッチリスト全体を保存する（監査 2023——追加・削除・変更されたエントリのアドレスと
    /// 表示名を Detail に含める。ADR-0018 決定 5）。差分がなければ保存も監査もしない。
    /// 保存後は再起動なしに即時反映される（決定 6）。
    /// </summary>
    /// <exception cref="WizardValidationException">クラス remarks の「拒否」に該当する場合。</exception>
    Task<SourceSilenceConfigureResult> ConfigureAsync(
        SourceSilenceSettings settings,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 画面で編集されるウォッチリスト 1 エントリ（ファイルの生値の形）。
/// </summary>
/// <param name="Address">監視する送信元アドレス（必須。保存時に正規化される）。</param>
/// <param name="Label">表示名（任意。ADR-0018 決定 2——装置との対応を運用者の語彙で保持する）。</param>
/// <param name="ThresholdMinutes">
/// このエントリの閾値（分）。<see langword="null"/> は「既定値で補完」——手編集で省略した
/// 既存エントリの保持用であり、<b>新規エントリでは指定必須</b>（決定 4）。
/// </param>
public sealed record SourceSilenceWatchlistItem(
    string Address,
    string? Label,
    int? ThresholdMinutes);

/// <summary>画面で編集されるウォッチリスト設定（全量置換）。</summary>
public sealed record SourceSilenceSettings(IReadOnlyList<SourceSilenceWatchlistItem> Watchlist);

/// <summary>
/// 現在の設定と稼働状態。
/// </summary>
/// <param name="DefaultThresholdMinutes">閾値を省略したエントリの補完値（実効値。表示用）。</param>
/// <param name="MaxWatchlistEntries">登録上限（表示・画面側検証用）。</param>
/// <param name="MinThresholdMinutes">エントリ閾値の下限（分）。</param>
/// <param name="MaxThresholdMinutes">エントリ閾値の上限（分）。</param>
/// <param name="Watchlist">ファイルの生値（正規化・検証前の保存内容）。</param>
/// <param name="RuntimeStates">
/// 稼働中の判定状態（正規化済みアドレス・実効閾値・途絶中フラグ）。不正で無効化された
/// エントリはここに現れない——<see cref="Watchlist"/> との突合で「保存されているが監視されて
/// いない」エントリを画面が識別できる。
/// </param>
public sealed record SourceSilenceAdminStatus(
    int DefaultThresholdMinutes,
    int MaxWatchlistEntries,
    int MinThresholdMinutes,
    int MaxThresholdMinutes,
    IReadOnlyList<SourceSilenceWatchlistItem> Watchlist,
    IReadOnlyList<YaguraSourceSilenceReading> RuntimeStates);

/// <summary>
/// 登録候補の 1 行（ADR-0018 決定 4。実受信のある送信元から選ばせる）。
/// </summary>
/// <param name="Address">送信元アドレス（保存データの表記のまま）。</param>
/// <param name="LastReceivedAt">最終受信時刻（UTC）。</param>
/// <param name="RecordCount">累計保存件数（受信ペースの近似表示に使う）。</param>
/// <param name="AlreadyRegistered">正規化後のアドレスがウォッチリストに登録済みか。</param>
public sealed record SourceSilenceCandidate(
    string Address,
    DateTimeOffset LastReceivedAt,
    long RecordCount,
    bool AlreadyRegistered);

/// <summary>保存の結果（監査 2023 に残した差分と、保存後の状態）。</summary>
public sealed record SourceSilenceConfigureResult(
    IReadOnlyList<string> AddedAddresses,
    IReadOnlyList<string> RemovedAddresses,
    IReadOnlyList<string> ChangedAddresses,
    SourceSilenceAdminStatus Status);
