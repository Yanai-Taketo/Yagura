namespace Yagura.Web.ForwarderKit;

/// <summary>
/// フォワーダキット生成の要求（ADR-0008 設計条件 1〜3・9）。宛先・ポート・収集チャネル・
/// MSI 同梱の要否を保持する。生成される <c>fluent-bit-yagura.conf</c> への注入文字列に
/// 任意の値を許さないため、構築時に必ず <see cref="TryCreate"/> の検証を経由させる
/// （設計条件 5「設定ファイルへの任意文字列注入を作らない」）。
/// </summary>
public sealed class ForwarderKitRequest
{
    private ForwarderKitRequest(
        string host,
        int port,
        IReadOnlyList<string> channels,
        ForwarderMsiBundle? msiBundle)
    {
        Host = host;
        Port = port;
        Channels = channels;
        MsiBundle = msiBundle;
    }

    /// <summary>宛先ホスト（IP アドレスまたはホスト名）。</summary>
    public string Host { get; }

    /// <summary>宛先ポート。</summary>
    public int Port { get; }

    /// <summary>
    /// 正規化済みの収集チャネル一覧（<see cref="ForwarderKitConstraints.KnownChannels"/> の
    /// 順序に揃え、重複を除いたもの）。
    /// </summary>
    public IReadOnlyList<string> Channels { get; }

    /// <summary>コンマ区切りのチャネル文字列（<c>fluent-bit-yagura.conf</c> の <c>@@CHANNELS@@</c> 置換値）。</summary>
    public string ChannelsValue => string.Join(",", Channels);

    /// <summary>
    /// MSI 同梱の要求（<see langword="null"/> = 非同梱。ADR-0008 設計条件 9）。
    /// 非 null は「同梱する」の意志表示であり、<see cref="ForwarderKitBuilder.Build"/> は
    /// これが非 null のときのみ MSI を ZIP に封入する。
    /// </summary>
    public ForwarderMsiBundle? MsiBundle { get; }

    /// <summary>MSI 同梱を要求したか。</summary>
    public bool IncludeMsi => MsiBundle is not null;

    /// <summary>
    /// 入力値を検証し、成功時は正規化済みの <see cref="ForwarderKitRequest"/> を返す。
    /// </summary>
    /// <param name="host">宛先ホスト（未指定・空文字はエラー）。</param>
    /// <param name="port">宛先ポート。</param>
    /// <param name="channels">
    /// 収集チャネル（コンマ区切り。前後の空白は許容し正規化時に除去する）。null・空文字は
    /// <see cref="ForwarderKitConstraints.DefaultChannels"/> として扱う。
    /// </param>
    /// <param name="msiBundle">
    /// MSI 同梱要求（<see langword="null"/> なら非同梱）。版不一致かつ未確認の場合は検証失敗
    /// （<see cref="ForwarderKitValidationError.MsiVersionMismatchNotAcknowledged"/>）とする
    /// ——画面側の二段階確認を通っていないリクエストに対する最終防御（ADR-0008 設計条件 9）。
    /// </param>
    /// <param name="request">検証成功時の結果。</param>
    /// <param name="error">検証失敗時のエラー内容。</param>
    /// <returns>検証に成功したか。</returns>
    public static bool TryCreate(
        string? host,
        int port,
        string? channels,
        ForwarderMsiBundle? msiBundle,
        out ForwarderKitRequest? request,
        out ForwarderKitValidationError? error)
    {
        request = null;

        if (string.IsNullOrWhiteSpace(host))
        {
            error = ForwarderKitValidationError.HostRequired;
            return false;
        }

        var trimmedHost = host.Trim();
        if (!ForwarderKitConstraints.HostRegex.IsMatch(trimmedHost))
        {
            error = ForwarderKitValidationError.HostInvalid;
            return false;
        }

        if (port < ForwarderKitConstraints.MinPort || port > ForwarderKitConstraints.MaxPort)
        {
            error = ForwarderKitValidationError.PortOutOfRange;
            return false;
        }

        if (!TryNormalizeChannels(channels, out var normalizedChannels))
        {
            error = ForwarderKitValidationError.ChannelsInvalid;
            return false;
        }

        if (msiBundle is { VersionMismatch: true, VersionMismatchAcknowledged: false })
        {
            error = ForwarderKitValidationError.MsiVersionMismatchNotAcknowledged;
            return false;
        }

        error = null;
        request = new ForwarderKitRequest(trimmedHost, port, normalizedChannels, msiBundle);
        return true;
    }

    /// <summary>
    /// <see cref="TryCreate(string?, int, string?, ForwarderMsiBundle?, out ForwarderKitRequest?, out ForwarderKitValidationError?)"/>
    /// の MSI 非同梱版（既存呼び出し元・テストの後方互換のためのオーバーロード）。
    /// </summary>
    public static bool TryCreate(
        string? host,
        int port,
        string? channels,
        out ForwarderKitRequest? request,
        out ForwarderKitValidationError? error) =>
        TryCreate(host, port, channels, msiBundle: null, out request, out error);

    /// <summary>
    /// チャネル指定を正規化する: 既知チャネル（System/Application/Security）の部分集合のみを許可し、
    /// <see cref="ForwarderKitConstraints.KnownChannels"/> の順序へ並べ替え、重複を除く。
    /// 未知の値・空要素（連続カンマ等）が 1 つでもあれば失敗とする。
    /// </summary>
    private static bool TryNormalizeChannels(string? channels, out IReadOnlyList<string> normalized)
    {
        if (string.IsNullOrWhiteSpace(channels))
        {
            normalized = ForwarderKitConstraints.DefaultChannels;
            return true;
        }

        if (!ForwarderKitConstraints.ChannelsRegex.IsMatch(channels))
        {
            normalized = [];
            return false;
        }

        var requested = channels
            .Split(',', StringSplitOptions.TrimEntries)
            .ToList();

        if (requested.Count == 0 || requested.Any(string.IsNullOrEmpty))
        {
            normalized = [];
            return false;
        }

        var requestedSet = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        if (requestedSet.Any(c => !ForwarderKitConstraints.KnownChannels.Contains(c, StringComparer.OrdinalIgnoreCase)))
        {
            normalized = [];
            return false;
        }

        normalized = ForwarderKitConstraints.KnownChannels
            .Where(known => requestedSet.Contains(known))
            .ToList();
        return true;
    }
}

/// <summary>
/// <see cref="ForwarderKitRequest.TryCreate(string?, int, string?, ForwarderMsiBundle?, out ForwarderKitRequest?, out ForwarderKitValidationError?)"/>
/// の検証失敗種別（呼び出し側が文言を出し分けるための分類。エラーメッセージそのものは
/// このクラスに持たせない——UI 側 <c>UiText</c> / エンドポイント側で言語・文脈ごとに組み立てる）。
/// </summary>
public enum ForwarderKitValidationError
{
    /// <summary>宛先ホストが未指定。</summary>
    HostRequired,

    /// <summary>宛先ホストが許可文字集合外。</summary>
    HostInvalid,

    /// <summary>ポート番号が範囲外（1〜65535 の外）。</summary>
    PortOutOfRange,

    /// <summary>収集チャネルが不正（未知の値・空要素・許可文字集合外）。</summary>
    ChannelsInvalid,

    /// <summary>
    /// MSI 同梱要求の版が検証済み版と異なるにもかかわらず、二段階確認（承認）が
    /// 済んでいない（ADR-0008 設計条件 9——画面の警告 → 確認を通っていないリクエストへの
    /// エンドポイント側の最終防御）。
    /// </summary>
    MsiVersionMismatchNotAcknowledged,
}

/// <summary>
/// MSI 同梱要求の内容（ADR-0008 設計条件 9）。<see cref="ForwarderKitBuilder"/> が ZIP へ
/// 封入する際に必要な情報と、版不一致確認の状態を保持する。
/// </summary>
/// <param name="FilePath">配置フォルダ内の MSI のフルパス（読み取り専用。ここから読み込む）。</param>
/// <param name="FileName">ZIP 内に封入する際のファイル名。</param>
/// <param name="ProductVersion">MSI の実効版（ProductVersion 優先。取得不能ならファイル名由来）。</param>
/// <param name="Sha256">MSI バイト列の SHA256（16 進小文字）。</param>
/// <param name="OfficialHashMatch">公式配布 SHA256 との照合結果。</param>
/// <param name="VersionMismatch">実効版が検証済み版と異なるか。</param>
/// <param name="VersionMismatchAcknowledged">
/// 版不一致を管理者が明示確認したか（<see cref="VersionMismatch"/> が <see langword="false"/> の
/// ときは無視される）。
/// </param>
public sealed record ForwarderMsiBundle(
    string FilePath,
    string FileName,
    string? ProductVersion,
    string Sha256,
    OfficialHashMatchResult OfficialHashMatch,
    bool VersionMismatch,
    bool VersionMismatchAcknowledged);
