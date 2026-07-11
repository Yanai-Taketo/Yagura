namespace Yagura.Abstractions.Administration;

/// <summary>
/// 初期セットアップウィザードの契約（configuration.md §3〜§7・ui.md §4「設定（ウィザード群）」。
/// M8-4。Issue #71）。書き込み系サービスであるため <see cref="IYaguraWriteService"/> を実装する
/// （security.md §1 L-5 の参照分離検査の対象——閲覧リスナ側コンポーネントから参照してはならない）。
/// </summary>
/// <remarks>
/// <para>
/// <b>セッション統治（configuration.md §5・§7）</b>: 進行状態は circuit のメモリではなく
/// サーバ側のセッション状態に置き、circuit 喪失後の再入で確定済みステップから再開できる。
/// 実装は無操作タイムアウト（CF-3 仮値 15 分）を適用する——初期セットアップは資格情報を
/// 扱わないため、タイムアウトで失われるものはないが、最終アクセス時刻の管理は
/// 本番昇格ウィザードと同じ機構に乗せる。
/// </para>
/// <para>
/// <b>確定操作の一回性（§7）</b>: <see cref="ApplyAsync"/> は冪等トークンを要求し、
/// 瞬断 → 再送で設定ファイルが二重に書き換えられない。
/// </para>
/// </remarks>
public interface ISetupWizardService : IYaguraWriteService
{
    /// <summary>現在のセッション状態を返す（セッションがなければ初期状態を開始する）。</summary>
    Task<SetupWizardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 1 ステップ分の入力値を検証してサーバ側セッションへ確定する（configuration.md §7
    /// 「ステップ完了ごとにサーバ側へ確定する」）。
    /// </summary>
    /// <exception cref="WizardValidationException">入力値が不正な場合（ステップは確定されない）。</exception>
    Task<SetupWizardSnapshot> ConfirmStepAsync(
        SetupWizardStep step,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 直前に確定したステップの確定を取り消し、そのステップへ戻る（前進専用ではなく、確定済みの
    /// 前ステップを再編集できるようにする——オーナー指摘）。確定済みの入力値は保持し、戻り先ステップの
    /// フォームに再表示できるようにする。取り消すのは「最後に確定した 1 ステップ」のみ。確認ステップを
    /// 取り消す場合は発行済みの適用用冪等トークンも破棄する（前ステップの値が変わり得るため確認は再度行う）。
    /// </summary>
    /// <exception cref="WizardValidationException">
    /// 既に適用済みの場合、または戻れる確定済みステップが存在しない（最初のステップにいる）場合。
    /// </exception>
    Task<SetupWizardSnapshot> GoBackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 確定済みの全ステップの内容で設定ファイル（yagura.json）を生成・保存する
    /// （configuration.md §1「初期設定はセットアップウィザードが生成する」・§3 の
    /// 「読み込み → 変更 → 検証 → 保存」+ 楽観競合検出）。管理操作として監査記録
    /// （2000 番台）の対象（security.md §4.1）。
    /// </summary>
    /// <param name="idempotencyToken">
    /// 確認ステップの確定時に発行される冪等トークン（<see cref="SetupWizardSnapshot.ApplyIdempotencyToken"/>）。
    /// 同一トークンでの再呼び出しは再適用せず <see cref="SetupWizardApplyResult.AlreadyApplied"/> を返す。
    /// </param>
    /// <param name="operatorAddress">操作者の接続元アドレス（監査記録用。security.md §4.1）。</param>
    /// <param name="operatorScheme">
    /// 操作者の認証方式（<c>"windows"</c>/<c>"app"</c>。ADR-0010 決定 6「誰が」欄の実効化——
    /// 認証を経由しない操作（既定の loopback 無認証）では <see langword="null"/>）。
    /// </param>
    /// <param name="operatorPrincipal">操作者の認証済み利用者名（同上。未認証では <see langword="null"/>）。</param>
    Task<SetupWizardApplyResult> ApplyAsync(
        string idempotencyToken,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default);
}
