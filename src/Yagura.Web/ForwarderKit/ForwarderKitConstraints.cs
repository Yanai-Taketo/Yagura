using System.Text.RegularExpressions;

namespace Yagura.Web.ForwarderKit;

/// <summary>
/// フォワーダキット生成の置換値検証・版表明の単一ソース（ADR-0008 設計条件 5・委任 #2/#3）。
/// </summary>
/// <remarks>
/// <para>
/// <b>宛先・チャネルの正規表現は <c>forwarder/fluent-bit/install.ps1</c> の
/// <c>ValidatePattern</c> 定義と同一でなければならない</b>（ADR-0008 設計条件 5）。
/// ここで定数化した文字列は、<c>ForwarderKitConstraintsSyncTests</c>
/// （<c>Yagura.Web.Tests</c>）がリポジトリの <c>install.ps1</c> を読み込んで機械的に突合する。
/// どちらか一方だけを変更すると当該テストが red になる——サーバ側実装と配布物側の
/// 検証制約の乖離を CI で検知する仕組み。
/// </para>
/// <para>
/// <b>検証済み Fluent Bit 版</b>（ADR-0008 委任 #3）: 表明の実体はここに置き、生成 UI・生成
/// README・利用者ガイド（<c>docs/guides/forward-windows-eventlog.md</c>）の間で一元化する。
/// 版を更新する場合は、実機検証を伴う版更新の記録を同じ PR に含めること
/// （conventions.md の実体検証原則）。<c>ForwarderKitVersionSyncTests</c> が本値と
/// 利用者ガイド・生成 README テンプレートの版表記の一致を固定する。
/// </para>
/// </remarks>
public static class ForwarderKitConstraints
{
    /// <summary>
    /// 宛先ホストの検証パターン（<c>install.ps1</c> の <c>$YaguraHost</c> の
    /// <c>ValidatePattern</c> と同一文字列）。
    /// </summary>
    public const string HostPattern = @"^[A-Za-z0-9\.\-:]+$";

    /// <summary>
    /// 収集チャネルの検証パターン（<c>install.ps1</c> の <c>$Channels</c> の
    /// <c>ValidatePattern</c> と同一文字列）。
    /// </summary>
    public const string ChannelsPattern = @"^[A-Za-z0-9,\- ]+$";

    /// <summary>ポート番号の下限（<c>install.ps1</c> の <c>ValidateRange(1, 65535)</c> と同一）。</summary>
    public const int MinPort = 1;

    /// <summary>ポート番号の上限。</summary>
    public const int MaxPort = 65535;

    /// <summary>静的キットと同じ既定ポート（<c>forward-windows-eventlog.md</c>）。</summary>
    public const int DefaultPort = 514;

    /// <summary>収集チャネルとして選択できる既知の値（この順序が正規化後の並び順になる）。</summary>
    public static readonly IReadOnlyList<string> KnownChannels = ["System", "Application", "Security"];

    /// <summary>既定で有効な収集チャネル（ADR-0008 設計条件 2。Security は初期オフのオプトイン）。</summary>
    public static readonly IReadOnlyList<string> DefaultChannels = ["System", "Application"];

    /// <summary>
    /// 検証済み Fluent Bit 版（<c>docs/guides/forward-windows-eventlog.md</c> §検証済み環境・
    /// <c>forwarder/fluent-bit/README.generated.md</c> と同期——ADR-0008 委任 #3）。
    /// </summary>
    public const string VerifiedFluentBitVersion = "5.0.8";

    /// <summary>宛先ホストの検証（コンパイル済み・スレッドセーフ）。</summary>
    public static readonly Regex HostRegex = new(HostPattern, RegexOptions.Compiled);

    /// <summary>収集チャネル文字列全体の検証（個々のチャネル名は <see cref="KnownChannels"/> で判定）。</summary>
    public static readonly Regex ChannelsRegex = new(ChannelsPattern, RegexOptions.Compiled);
}

/// <summary>
/// MSI オプトイン同梱（ADR-0008 設計条件 9・委任 #7）の定数。配置フォルダ・ファイル名パターン・
/// 公式配布 SHA256 の器を <see cref="ForwarderKitConstraints"/> とは別クラスに分離する
/// ——設計条件 9 は既存の設計条件 1〜8 とは別の改訂（2026-07-07 amendment）で追加された事項であり、
/// 将来 security.md へ実装細部を一本化する際（ADR-0008 改訂履歴 1 の申し送り）に本クラス単位で
/// 移動しやすくする狙いもある。
/// </summary>
public static class ForwarderMsiConstraints
{
    /// <summary>
    /// 配置フォルダのデータルートからの相対パス（<c>%ProgramData%\Yagura\forwarder\</c>。
    /// ADR-0008 設計条件 9）。フォルダの作成・ACL 設定はインストーラ（WiX）の領分であり、
    /// 本アプリはここを読み取るのみ（フォルダが無ければ未検出として扱う）。
    /// </summary>
    public const string PlacementSubPath = "forwarder";

    /// <summary>
    /// MSI ファイル名パターン（x64。ADR-0008 設計条件 9）。実際の判定は
    /// <see cref="ForwarderMsiFilter.IsCandidateFileName(string)"/>（正規表現をコンパイル済みで保持）。
    /// ここでは人が読める表記として残す。ARM64 は <see cref="FileNamePatternArm64"/>
    /// （ADR-0009 決定7・委任 #4）。
    /// </summary>
    public const string FileNamePattern = "fluent-bit-*-win64.msi";

    /// <summary>
    /// MSI ファイル名パターン（ARM64。ADR-0009 決定7・委任 #4）。Fluent Bit は公式に Windows
    /// ARM64 向け MSI を <c>winarm64</c> サフィックスで配布している
    /// （<c>https://packages.fluentbit.io/windows/</c>、2026-07-10 ライブ確認）。
    /// </summary>
    public const string FileNamePatternArm64 = "fluent-bit-*-winarm64.msi";

    /// <summary>
    /// 検証済み Fluent Bit 版（<see cref="ForwarderKitConstraints.VerifiedFluentBitVersion"/>）に
    /// 対応する公式配布 MSI（x64）の SHA256（16 進小文字）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>2026-07-10 ライブ検証で確定</b>: <c>https://packages.fluentbit.io/windows/</c>
    /// （公式配布ドメイン・HTTPS/TLS 検証済み）から <c>fluent-bit-5.0.8-win64.msi</c> を取得し、
    /// <c>Get-FileHash -Algorithm SHA256</c> で算出した値。Fluent Bit は個別パッケージ向けの
    /// 署名済みチェックサムファイル（<c>.sha256</c> 等）を公開していないため、「公式ハッシュ」の
    /// 実体は「公式ドメインから TLS 経由で取得した実ファイルの実測値」である
    /// （conventions.md の実体検証原則：公式ドキュメント引用または実機確認）。
    /// </para>
    /// <para>
    /// 版を上げる場合は、同じ手順で新版のハッシュを live 再取得してから更新すること。
    /// 偽のハッシュ値をハードコードすると「一致した」という誤った安心を生むため、確定した
    /// 値を live 検証できない間は <see langword="null"/> のまま保つ——未設定時は
    /// <see cref="ForwarderMsiFilter.MatchesOfficialHash"/> が
    /// <see cref="OfficialHashMatchResult.Unverified"/> を返し、生成画面・README で
    /// 「公式ハッシュとの照合は未実施」の旨を出す（安全側。ADR-0008 設計条件 9）。
    /// </para>
    /// </remarks>
    public const string? OfficialSha256ForVerifiedVersion = "f0649d52bd681d6a4ed4234a669a6d2b09ce1945ca8efcee59b1b807222374d8";

    /// <summary>
    /// 検証済み Fluent Bit 版に対応する公式配布 MSI（ARM64）の SHA256（16 進小文字）。
    /// </summary>
    /// <remarks>
    /// <b>2026-07-10 ライブ検証で確定</b>（ADR-0009 決定7・委任 #4）: 同日
    /// <c>https://packages.fluentbit.io/windows/fluent-bit-5.0.8-winarm64.msi</c>
    /// （23,796,327 バイト）を取得し、<c>Get-FileHash -Algorithm SHA256</c> で算出した値。
    /// 検証手順・根拠は <see cref="OfficialSha256ForVerifiedVersion"/> の remarks と同じ
    /// （このプロジェクトの検証環境は x64 のため、実行検証ではなく取得物のハッシュ実測に留まる。
    /// この検証環境では winarm64 バイナリ自体は実行できない）。
    /// </remarks>
    public const string? OfficialSha256ForVerifiedVersionArm64 = "9730cd2479276b2fd8f323c8c5ddbfe6be52e2f4e8ebb3caae1efda46d327860";

    /// <summary>指定アーキテクチャの人が読めるファイル名パターン表記を返す。</summary>
    public static string GetFileNamePattern(ForwarderMsiArchitecture architecture) => architecture switch
    {
        ForwarderMsiArchitecture.WinArm64 => FileNamePatternArm64,
        _ => FileNamePattern,
    };

    /// <summary>指定アーキテクチャの検証済み版に対応する公式配布 SHA256（未確定なら <see langword="null"/>）。</summary>
    public static string? GetOfficialSha256(ForwarderMsiArchitecture architecture) => architecture switch
    {
        ForwarderMsiArchitecture.WinArm64 => OfficialSha256ForVerifiedVersionArm64,
        _ => OfficialSha256ForVerifiedVersion,
    };
}
