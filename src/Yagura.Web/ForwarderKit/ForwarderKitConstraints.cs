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
    public const string VerifiedFluentBitVersion = "4.0.14";

    /// <summary>宛先ホストの検証（コンパイル済み・スレッドセーフ）。</summary>
    public static readonly Regex HostRegex = new(HostPattern, RegexOptions.Compiled);

    /// <summary>収集チャネル文字列全体の検証（個々のチャネル名は <see cref="KnownChannels"/> で判定）。</summary>
    public static readonly Regex ChannelsRegex = new(ChannelsPattern, RegexOptions.Compiled);
}
