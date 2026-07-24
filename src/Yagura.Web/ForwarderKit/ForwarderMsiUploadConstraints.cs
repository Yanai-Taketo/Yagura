namespace Yagura.Web.ForwarderKit;

/// <summary>
/// フォワーダ MSI アップロード（配置経路 (b)。ADR-0020 決定 3）の制約定数。
/// </summary>
public static class ForwarderMsiUploadConstraints
{
    /// <summary>
    /// アップロードのサイズ上限（仮値 100 MiB。確定は ADR-0020 委任 4——Fluent Bit の
    /// win64/winarm64 MSI の実測サイズ（数十 MB 級）に十分な余裕を持たせた値）。
    /// Content-Length 申告はこの値で本文を読む前に事前拒否し、申告なし・虚偽申告は
    /// ストリーミング累積カウントで打ち切る（両方を実装する——ADR-0020 決定 3）。
    /// </summary>
    public const long MaxUploadBytes = 100L * 1024 * 1024;

    /// <summary>
    /// 配置先ボリュームに常に残すべき空き容量の下限（仮値 1 GiB）。
    /// 受付判定は「書き込み完了後もこの値を下回らないこと」（= 空き − 申告サイズ ≥ 本値。
    /// 申告なしは <see cref="MaxUploadBytes"/> 基準）——アップロードを受け付けた直後に
    /// 空き容量警告（1006）圏へ突入する設計にしない（ADR-0020 決定 3・再レビュー鈴木指摘）。
    /// 値は 1006 の警告閾値（<c>ActiveNotificationConstants.MonitoredVolumeFreeSpaceMinBytes</c> =
    /// 1 GiB）と揃える——両者の同期は ADR-0020 委任 4 の確定事項（現状は仮値どうしの一致）。
    /// </summary>
    public const long FreeSpaceFloorBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// ステージングファイル名の接頭辞（ADR-0020 決定 3）。検出パターン
    /// （<see cref="ForwarderMsiConstraints.FileNamePattern"/> = <c>fluent-bit-*</c>）に一致しない
    /// 名前であることが要件——中間状態を <see cref="IForwarderMsiSource.Lookup"/>・生成処理に
    /// 晒さない。この不可視性は回帰テストで固定する（ADR-0020 決定 5 ②）。
    /// </summary>
    public const string StagingFileNamePrefix = ".uploading-";

    /// <summary>孤児ステージングファイルの掃除に使う検索パターン。</summary>
    public const string StagingFileSearchPattern = ".uploading-*.msi";

    /// <summary>
    /// 書き込み可否の実挙動検出（ADR-0020 決定 2・委任 2）に使うプローブファイル名の接頭辞。
    /// 検出パターン非一致・作成後に即削除。
    /// </summary>
    public const string WriteProbeFileNamePrefix = ".writecheck-";
}
