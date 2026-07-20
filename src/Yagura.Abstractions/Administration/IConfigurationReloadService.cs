namespace Yagura.Abstractions.Administration;

/// <summary>
/// 設定ファイル（手編集）のライブ再読み込み（configuration.md §3。CF-4 層1。Issue #262）。
/// UI（管理リスナ）と SCM カスタム制御コード（CF-5。<c>sc control Yagura 128</c>）の両方から
/// 同じ実装が呼ばれる。
/// </summary>
/// <remarks>
/// 書き込み系契約（<see cref="IYaguraWriteService"/>）である——再読み込みは実行中の構成を
/// 変更する管理操作であり、閲覧リスナから到達可能であってはならない（security.md §1 L-5 の
/// 参照分離検査の対象）。
/// </remarks>
public interface IConfigurationReloadService : IYaguraWriteService
{
    /// <summary>
    /// 設定ファイルを読み直し、差分を計算して、即時反映できるキーを適用する。
    /// 検証失敗（起動失敗分類の不正値）の場合は何も適用せず、旧設定のまま
    /// <see cref="ConfigurationReloadResult.Rejected"/> を返す（実行中のプロセスを
    /// 設定事故で止めない——起動時の fail-fast とは非対称であることが仕様）。
    /// </summary>
    /// <param name="operatorAddress">操作者の接続元（監査記録用。SCM 経由は <see langword="null"/>）。</param>
    /// <param name="authenticationScheme">認証方式（監査記録用。未認証は <see langword="null"/>）。</param>
    /// <param name="authenticatedPrincipal">認証済み利用者名（監査記録用）。</param>
    Task<ConfigurationReloadResult> ReloadAsync(
        string? operatorAddress,
        string? authenticationScheme,
        string? authenticatedPrincipal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 再起動待ちのまま残っているキーの現在スナップショットを返す（Issue #286。認証済み
    /// 管理面の常設表示用の読み取り口——再読み込みを実行せず、状態を変更しない）。
    /// キー昇順。空 = 再起動待ちなし（表示は再起動で自然に消える——プロセス内状態のため）。
    /// </summary>
    IReadOnlyList<PendingRestartKey> GetPendingRestartKeys();
}

/// <summary>
/// 再起動待ちのまま残っている設定キー 1 件（Issue #286。管理面の常設表示の表示単位）。
/// </summary>
/// <param name="Key">設定キー（JSON キーパス）。</param>
/// <param name="DetectedAt">
/// このキーの変更を最初に検出した再読み込みの時刻（UTC）。以後の再読み込みで同じキーが
/// 再び変更されても更新しない——「いつから未反映のまま残っているか」を表す。
/// </param>
public sealed record PendingRestartKey(string Key, DateTimeOffset DetectedAt);

/// <summary>
/// 再読み込み 1 回の結果（configuration.md §3「未反映のまま残る項目を…明示する」の入力）。
/// </summary>
/// <param name="Rejected">検証失敗により何も適用されなかったか（旧設定のまま継続）。</param>
/// <param name="RejectionReason">検証失敗の理由（<paramref name="Rejected"/> が真のとき）。</param>
/// <param name="ChangedKeys">前回適用時点からの変更キー。</param>
/// <param name="AppliedKeys">今回の再読み込みで実際に反映されたキー。</param>
/// <param name="PendingRestartKeys">
/// 変更されたが反映にサービス再起動（または層2 のリスナ再構成）を要し、未反映のまま残る
/// キー（過去の再読み込みからの累積——再起動まで表示され続ける）。
/// </param>
/// <param name="WarningMessages">検証警告（不正値の既定値フォールバック等）の説明文。</param>
/// <param name="UnknownKeys">未知キー（タイポ検出。configuration.md §1）。</param>
/// <param name="TypeCoercionNotes">
/// 型を読み替えて受理したキー（数値・真偽値を文字列として受理）の説明文。警告ではなく情報
/// ——受理は正常系であり、未知キー・既定値への差し替えと同じ場所に並べて表示する
/// （configuration.md §1。Issue #334）。
/// </param>
public sealed record ConfigurationReloadResult(
    bool Rejected,
    string? RejectionReason,
    IReadOnlyList<string> ChangedKeys,
    IReadOnlyList<string> AppliedKeys,
    IReadOnlyList<string> PendingRestartKeys,
    IReadOnlyList<string> WarningMessages,
    IReadOnlyList<string> UnknownKeys,
    IReadOnlyList<string> TypeCoercionNotes)
{
    /// <summary>変更がなかったか（適用・未反映とも空）。</summary>
    public bool HasChanges => ChangedKeys.Count > 0;
}
