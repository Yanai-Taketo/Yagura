namespace Yagura.Web.ForwarderKit;

/// <summary>
/// 配置フォルダ（データルート配下 <c>forwarder</c>）への MSI の配置（アップロード）・削除を担う
/// 書き込み側の契約（ADR-0020 決定 2・3。配置経路 (b)）。読み取り側（<see cref="IForwarderMsiSource"/>）
/// とは契約を分離する——読み取りは既定構成で常に使われるのに対し、書き込みは
/// 「認証有効 + loopback 認証 opt-in + 機能 opt-in + 管理者の明示 ACE 付与」（ADR-0020 決定 1〜2）が
/// 揃った構成でのみ意味を持つため。
/// </summary>
/// <remarks>
/// <para>
/// <b>二段階の配置フロー（stage → commit）</b>: ADR-0020 決定 3 は「置換時は旧 SHA256・新 SHA256 を
/// 並べて表示したうえで明示確認」「公式ハッシュ不一致は二段階確認」を要求する。SHA256 と
/// ProductVersion はサーバがファイルを受け取って初めて計算できるため、配置は
/// ①<see cref="StageAsync"/>（ステージング書き込み + 検証 + 計算結果の返却）と
/// ②<see cref="Commit"/>（確認フラグを受けてのアトミックリネーム）の二段階に分ける。
/// ステージングファイルは検出パターン（<see cref="ForwarderMsiConstraints.FileNamePattern"/>）に
/// 一致しない名前を使い、<see cref="IForwarderMsiSource.Lookup"/>・生成処理から構造的に不可視とする。
/// </para>
/// <para>
/// <b>排他はプロセス全体で単一飛行</b>（ADR-0020 決定 3）: 進行中のステージングがあるとき、
/// アーキテクチャの別を問わず後続の <see cref="StageAsync"/> は
/// <see cref="ForwarderMsiStageError.AnotherUploadInProgress"/> で拒否する。
/// </para>
/// <para>
/// <b>ACL は変更しない</b>（ADR-0020 決定 2）: 本契約の実装は配置フォルダの ACL に一切触れない。
/// 書き込み ACE が付与されていない既定構成では、全操作が
/// <see cref="ForwarderMsiWriteAccess.CanWrite"/> = false / 書き込み失敗として現れる——
/// それが仕様である（書き込み経路の開放は OS 管理者の明示操作のみ）。
/// </para>
/// </remarks>
public interface IForwarderMsiStore
{
    /// <summary>配置フォルダのフルパス（画面表示・手順案内用）。</summary>
    string FolderPath { get; }

    /// <summary>
    /// 書き込み経路が開放されているか（ACE 付与済みか）を実挙動で検出する（ADR-0020 決定 2・委任 2）。
    /// プローブファイル（検出パターン非一致・即削除）の作成試行で判定する。
    /// </summary>
    ForwarderMsiWriteAccess CheckWriteAccess();

    /// <summary>
    /// 中断で残った孤児ステージングファイルを削除する（ADR-0020 決定 3）。起動時と
    /// 新規アップロード開始時に呼ばれる。削除できた件数を返す（書き込み権限が無い場合は 0——
    /// 孤児が存在し得るのは書き込みできた期間だけなので、権限喪失後の残骸は ACE 再付与時に掃除される）。
    /// </summary>
    int CleanupStagingFiles();

    /// <summary>
    /// アップロード本文をステージングへ書き込み、検証（サイズ・ProductVersion・SHA256・公式ハッシュ照合・
    /// 既存ファイルとの関係）を行う（ADR-0020 決定 3）。成功時は確認待ちの保留状態になり、
    /// <see cref="Commit"/> / <see cref="Discard"/> で決着させる。
    /// </summary>
    /// <param name="architecture">配置先アーキテクチャ（win64 / winarm64）。</param>
    /// <param name="content">アップロード本文のストリーム（エンドポイントの要求本文）。</param>
    /// <param name="declaredLength">Content-Length 申告値（未申告は null——上限はストリーミング累積で強制）。</param>
    /// <param name="cancellationToken">中断（クライアント切断等）。中断時はステージングを削除する。</param>
    Task<ForwarderMsiStageResult> StageAsync(
        ForwarderMsiArchitecture architecture,
        Stream content,
        long? declaredLength,
        CancellationToken cancellationToken);

    /// <summary>
    /// 確認済みの保留ステージングを正式ファイル名へアトミックにリネームして配置を確定する
    /// （ADR-0020 決定 3）。置換時は同一アーキの旧ファイルを除去し、単一状態を保証する。
    /// </summary>
    /// <param name="stagingToken"><see cref="ForwarderMsiStageResult.StagingToken"/>。</param>
    /// <param name="versionMismatchAcknowledged">
    /// 公式ハッシュ不一致（未知の版を含む）の二段階確認を利用者が明示承認したか。
    /// 不一致なのに未承認の commit は拒否する。
    /// </param>
    /// <param name="replaceAcknowledged">既存ファイルの置換を利用者が明示承認したか。</param>
    ForwarderMsiCommitResult Commit(string stagingToken, bool versionMismatchAcknowledged, bool replaceAcknowledged);

    /// <summary>保留ステージングを破棄する（利用者の中止。ADR-0020 決定 4——中止も監査対象）。</summary>
    ForwarderMsiDiscardResult Discard(string stagingToken);

    /// <summary>
    /// 配置済み MSI を削除する（ADR-0020 決定 3。常に二段階確認を経た呼び出しのみ）。
    /// <paramref name="expectedSha256"/> は画面が確認時に表示した SHA256——実ファイルと一致しない
    /// 場合（表示と確定の間に差し替わった場合）は削除しない（TOCTOU ガード）。
    /// </summary>
    ForwarderMsiDeleteResult Delete(ForwarderMsiArchitecture architecture, string expectedSha256);
}

/// <summary>書き込み経路の開放状態（ADR-0020 決定 2）。</summary>
/// <param name="CanWrite">書き込みが物理的に成立するか（= ACE 付与済みか）。</param>
/// <param name="FailureReason">不成立時の内部理由（ログ・画面の補足表示用。null 可）。</param>
public sealed record ForwarderMsiWriteAccess(bool CanWrite, string? FailureReason = null);

/// <summary><see cref="IForwarderMsiStore.StageAsync"/> の失敗理由。</summary>
public enum ForwarderMsiStageError
{
    /// <summary>別のアップロードが進行中（プロセス全体で単一飛行）。</summary>
    AnotherUploadInProgress,

    /// <summary>Content-Length 申告がサイズ上限を超えている（本文を読む前の事前拒否）。</summary>
    DeclaredLengthExceedsLimit,

    /// <summary>ストリーミング中の累積がサイズ上限を超えた（申告なし・虚偽申告の打ち切り）。</summary>
    StreamExceedsLimit,

    /// <summary>
    /// 配置先ボリュームの空き容量が不足（「書き込み完了後も 1006 の警告閾値を下回らない」判定。
    /// ADR-0020 決定 3）。
    /// </summary>
    InsufficientDiskSpace,

    /// <summary>書き込み経路が未開放（ACE 未付与）等でステージング書き込みに失敗した。</summary>
    WriteFailed,

    /// <summary>
    /// MSI の ProductVersion を読み取れなかった。アップロードではクライアント申告のファイル名を
    /// 信用しないため、版の根拠が ProductVersion 以外に存在せず、読み取り失敗は拒否になる
    /// （検出側のファイル名フォールバックは適用しない——ADR-0020 決定 3）。
    /// </summary>
    ProductVersionUnreadable,

    /// <summary>ProductVersion から正式ファイル名を構成できない（不正な文字を含む等）。</summary>
    ProductVersionInvalid,

    /// <summary>
    /// 同一アーキの既存 MSI が複数ある（手動配置由来の多重状態）。アップロードによる置換先を
    /// 一意に決められないため拒否し、手動での単一化を案内する（安全側——ADR-0008 設計条件 9 の
    /// 「複数はエラー停止」と同じ判断）。
    /// </summary>
    MultipleExistingFiles,

    /// <summary>中断（クライアント切断・キャンセル）。ステージングは削除済み。</summary>
    Cancelled,
}

/// <summary>
/// <see cref="IForwarderMsiStore.StageAsync"/> の結果。成功時は確認待ちの保留状態
/// （<see cref="StagingToken"/> で <see cref="IForwarderMsiStore.Commit"/> /
/// <see cref="IForwarderMsiStore.Discard"/> に引き渡す）。
/// </summary>
public sealed record ForwarderMsiStageResult(
    bool Success,
    ForwarderMsiStageError? Error = null,
    string? StagingToken = null,
    string? FinalFileName = null,
    string? ProductVersion = null,
    string? Sha256 = null,
    long Length = 0,
    OfficialHashMatchResult OfficialHashMatch = OfficialHashMatchResult.Unverified,
    bool VersionMismatch = false,
    string? ExistingFileName = null,
    string? ExistingSha256 = null)
{
    public static ForwarderMsiStageResult Failed(ForwarderMsiStageError error) => new(false, error);
}

/// <summary><see cref="IForwarderMsiStore.Commit"/> の失敗理由。</summary>
public enum ForwarderMsiCommitError
{
    /// <summary>保留ステージングが存在しない・トークン不一致（期限切れ・掃除済みを含む）。</summary>
    UnknownStagingToken,

    /// <summary>公式ハッシュ不一致なのに二段階確認が未承認。</summary>
    VersionMismatchNotAcknowledged,

    /// <summary>既存ファイルの置換なのに置換確認が未承認。</summary>
    ReplaceNotAcknowledged,

    /// <summary>
    /// ステージング後に配置フォルダの状態が変わった（既存ファイルの出現・消失・差し替え）。
    /// 表示した確認内容と実状態が食い違うため確定しない（TOCTOU ガード）。再アップロードを要する。
    /// </summary>
    FolderStateChanged,

    /// <summary>リネーム・旧ファイル除去の書き込みに失敗した。</summary>
    WriteFailed,
}

/// <summary><see cref="IForwarderMsiStore.Commit"/> の結果。</summary>
public sealed record ForwarderMsiCommitResult(
    bool Success,
    ForwarderMsiCommitError? Error = null,
    string? FinalFileName = null,
    string? ProductVersion = null,
    string? Sha256 = null,
    long Length = 0,
    OfficialHashMatchResult OfficialHashMatch = OfficialHashMatchResult.Unverified,
    bool VersionMismatch = false,
    bool VersionMismatchAcknowledged = false,
    string? ReplacedSha256 = null)
{
    public static ForwarderMsiCommitResult Failed(ForwarderMsiCommitError error) => new(false, error);
}

/// <summary><see cref="IForwarderMsiStore.Discard"/> の結果（記録用の最小情報）。</summary>
public sealed record ForwarderMsiDiscardResult(bool Found, string? Sha256 = null, string? ProductVersion = null);

/// <summary><see cref="IForwarderMsiStore.Delete"/> の失敗理由。</summary>
public enum ForwarderMsiDeleteError
{
    /// <summary>対象アーキの配置済み MSI が存在しない。</summary>
    NotFound,

    /// <summary>対象アーキの MSI が複数ある（削除対象を一意に決められない）。</summary>
    MultipleExistingFiles,

    /// <summary>実ファイルの SHA256 が確認時の表示と一致しない（TOCTOU ガード）。</summary>
    Sha256Mismatch,

    /// <summary>削除の書き込みに失敗した（ACE 未付与を含む）。</summary>
    WriteFailed,
}

/// <summary><see cref="IForwarderMsiStore.Delete"/> の結果（削除前 SHA256 を監査に残す——ADR-0020 決定 3）。</summary>
public sealed record ForwarderMsiDeleteResult(
    bool Success,
    ForwarderMsiDeleteError? Error = null,
    string? DeletedFileName = null,
    string? DeletedSha256 = null);
