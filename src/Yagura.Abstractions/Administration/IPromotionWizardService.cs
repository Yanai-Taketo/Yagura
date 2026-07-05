namespace Yagura.Abstractions.Administration;

/// <summary>
/// 本番昇格（SQLite → SQL Server）ウィザードの契約（database.md §6.1・ui.md §4。M8-4。Issue #71）。
/// 書き込み系サービスであるため <see cref="IYaguraWriteService"/> を実装する（security.md §1 L-5）。
/// </summary>
/// <remarks>
/// <para>
/// <b>準備と本番の分離（database.md §6.1）</b>: 接続検証（<see cref="ValidateConnectionAsync"/>）は
/// 準備フェーズであり、何度でも中断・再開できる。切替実行（<see cref="ExecuteAsync"/>）は
/// すべての事前検証が通ってから実行する。
/// </para>
/// <para>
/// <b>資格情報の統治（configuration.md §5）</b>: 接続文字列（資格情報を含む）は
/// 「ウィザードの 1 実行」の単位でメモリ内にのみ保持し、完了・失敗・無操作タイムアウト
/// （CF-3 仮値 15 分）で破棄する。サーバ側セッション状態（進行状況）には含めない——
/// タイムアウト・circuit 喪失後の再開では確定済みステップは残るが接続文字列は再入力を要する。
/// ディスク・ログ・監査記録のいずれにも書かない（監査記録は「使用した」事実のみ）。
/// </para>
/// <para>
/// <b>M8-4 骨格の範囲</b>: 切替本番の手順①〜④（書き込み停止 → システムイベント複写 →
/// 差し替え → drain。database.md §6.1）の実行時無瞬断切替は本骨格に含まれない。
/// <see cref="ExecuteAsync"/> は検証済み接続を設定ファイルへ保存し、現時点の実効の反映方式
/// （サービス再起動。configuration.md §8 の <c>Storage:Provider</c> 行）を結果として返す。
/// 旧・組み込み DB ファイルの処分（退避 / 削除）は選択の記録と監査までを骨格とし、
/// 実ファイル操作は切替の実行時手順の実装（後続 Issue）に含める。
/// </para>
/// </remarks>
public interface IPromotionWizardService : IYaguraWriteService
{
    /// <summary>現在のセッション状態を返す（セッションがなければ初期状態を開始する）。</summary>
    Task<PromotionWizardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// SQL Server への接続文字列を設定する（メモリ内保持のみ。上記 remarks の資格情報統治）。
    /// 設定すると検証済み状態はリセットされる（検証は現に保持している接続文字列に対してのみ有効）。
    /// </summary>
    /// <exception cref="WizardValidationException">接続文字列が空の場合。</exception>
    Task<PromotionWizardSnapshot> SetConnectionStringAsync(
        string connectionString,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 保持中の接続文字列で SQL Server への接続を検証する（database.md §6.1 の準備フェーズ。
    /// 管理者資格情報を使用した事実を監査記録（2000 番台）へ記録する——資格情報そのものは
    /// 記録しない）。実際の接続試行は接続検証の抽象
    /// （<c>Yagura.Host.Administration.ISqlServerConnectionValidator</c>）へ委譲される——
    /// SQL Server のない開発機でもテスト実装で経路を検証できる形にする（Issue #71 の要件）。
    /// </summary>
    Task<PromotionValidationResult> ValidateConnectionAsync(
        string? operatorAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>旧・組み込み DB ファイルの処分方法を選択する（database.md §6.1「残置しない」）。</summary>
    Task<PromotionWizardSnapshot> ChooseOldDatabaseDisposalAsync(
        OldDatabaseDisposal disposal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 切替を実行する（検証済み接続を設定ファイルへ保存する。管理操作として監査記録の対象。
    /// 冪等トークンにより二重適用を防ぐ——configuration.md §7）。
    /// </summary>
    Task<PromotionApplyResult> ExecuteAsync(
        string idempotencyToken,
        string? operatorAddress = null,
        CancellationToken cancellationToken = default);
}
