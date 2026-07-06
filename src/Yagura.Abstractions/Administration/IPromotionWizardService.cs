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
/// <b>資格情報の統治（configuration.md §5）</b>: 破棄の対象となる資格情報は<b>パスワードのみ</b>。
/// 「ウィザードの 1 実行」の単位でメモリ内にのみ保持し、完了・失敗・無操作タイムアウト
/// （CF-3 仮値 15 分）で破棄する。接続の項目（サーバ名・データベース名・認証方式・ユーザー名・
/// 証明書の信頼）とパスワードを含まない直接入力の接続文字列は秘密ではなく、進行状態として
/// タイムアウトを越えて保持する。タイムアウトはパスワードとともに検証済み状態・実行トークンも
/// 無効化する（検証は「現に保持している入力」に対してのみ有効）。パスワードはディスク・ログ・
/// 監査記録のいずれにも書かない（監査記録は「使用した」事実のみ）。
/// </para>
/// <para>
/// <b>入力方式の相互上書き規則（database.md §6.1 の項目入力/直接入力の併存）</b>:
/// <see cref="SetConnectionFormAsync"/> と <see cref="SetRawConnectionStringAsync"/> は最後に
/// 呼ばれた方が有効な入力方式となる（もう一方の入力内容は保持されるが、検証・実行には使われ
/// ない）。いずれの呼び出しも検証済み状態・実行トークンをリセットする。
/// </para>
/// <para>
/// <b>M8-4 骨格の範囲</b>: 切替本番の手順①〜④（書き込み停止 → システムイベント複写 →
/// 差し替え → drain。database.md §6.1）の実行時無瞬断切替は本骨格に含まれない。
/// <see cref="ExecuteAsync"/> は検証済み接続を設定ファイルへ保存し、現時点の実効の反映方式
/// （サービス再起動。configuration.md §8 の <c>Storage:Provider</c> 行）を結果として返す。
/// 旧・組み込み DB ファイルの処分（退避 / 削除）は選択の記録と監査までを骨格とし、
/// 実ファイル操作・退避先の存在/書込可否の事前検証は切替の実行時手順の実装（後続 Issue）に
/// 含める。
/// </para>
/// </remarks>
public interface IPromotionWizardService : IYaguraWriteService
{
    /// <summary>現在のセッション状態を返す（セッションがなければ初期状態を開始する）。</summary>
    Task<PromotionWizardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 接続の項目入力を設定する（database.md §6.1——接続文字列はサーバ側で組み立てる）。
    /// パスワードは <paramref name="password"/> でのみ受け取り、メモリ内にのみ保持する
    /// （上記 remarks の資格情報統治）。Windows 統合認証ではパスワードは受け取らず破棄する。
    /// 設定すると検証済み状態はリセットされる。
    /// </summary>
    /// <param name="form">接続の項目（パスワードを含まない）。</param>
    /// <param name="password">
    /// SQL Server 認証のパスワード（Windows 統合認証では無視。null/空白は「未入力」として扱う）。
    /// </param>
    /// <exception cref="WizardValidationException">必須項目が欠けている場合。</exception>
    Task<PromotionWizardSnapshot> SetConnectionFormAsync(
        PromotionConnectionForm form,
        string? password = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 接続文字列の直接入力を設定する（上級者向け——database.md §6.1）。
    /// <b>パスワード系キー（<c>Password</c>/<c>Pwd</c> 別名を含む）の記載は拒否する</b>——
    /// 正規化パースの上で判定し、パスワードは <paramref name="password"/> でのみ受け取る
    /// （画面・snapshot に平文が現れる経路を作らない）。設定すると検証済み状態はリセットされる。
    /// </summary>
    /// <param name="connectionString">パスワードを含まない接続文字列。</param>
    /// <param name="password">SQL Server 認証のパスワード（null/空白は「未入力」として扱う）。</param>
    /// <exception cref="WizardValidationException">
    /// 接続文字列が空・解釈不能・パスワード系キーを含む場合。
    /// </exception>
    Task<PromotionWizardSnapshot> SetRawConnectionStringAsync(
        string connectionString,
        string? password = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 保持中の接続入力で SQL Server への接続を検証する（database.md §6.1 の準備フェーズ。
    /// 管理者資格情報を使用した事実・成否・失敗の分類・証明書信頼の選択値を監査記録
    /// （2000 番台）へ記録する——資格情報そのもの・修復 SQL の原文は記録しない）。
    /// 失敗時は原因の分類と、ログイン失敗・データベース不在の場合は修復 SQL を返す
    /// （<b>提示のみでありサーバは実行しない</b>——database.md §5.2）。実際の接続試行は
    /// 接続検証の抽象（<c>Yagura.Host.Administration.ISqlServerConnectionValidator</c>）へ
    /// 委譲される——SQL Server のない開発機でもテスト実装で経路を検証できる形にする
    /// （Issue #71 の要件）。
    /// </summary>
    Task<PromotionValidationResult> ValidateConnectionAsync(
        string? operatorAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 旧・組み込み DB ファイルの処分方法を選択する（database.md §6.1「残置しない」）。
    /// 退避の選択時は退避先（絶対パス）の指定を必須とする（同 §6.1——選択内容・退避先は
    /// 監査記録の対象。存在・書込可否の事前検証は実処分の実装 = 後続 Issue の要件）。
    /// </summary>
    /// <param name="disposal">処分方法。</param>
    /// <param name="evacuationDirectory">退避先のフォルダ（退避の場合は必須・絶対パス。削除の場合は無視）。</param>
    /// <exception cref="WizardValidationException">退避なのに退避先が空・相対パスの場合。</exception>
    Task<PromotionWizardSnapshot> ChooseOldDatabaseDisposalAsync(
        OldDatabaseDisposal disposal,
        string? evacuationDirectory = null,
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
