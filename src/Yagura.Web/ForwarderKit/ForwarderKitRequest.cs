namespace Yagura.Web.ForwarderKit;

/// <summary>
/// フォワーダキット生成の要求（ADR-0008 設計条件 1〜3）。宛先・ポート・収集チャネルを
/// 保持する。生成される <c>fluent-bit-yagura.conf</c> への注入文字列に任意の値を許さないため、
/// 構築時に必ず <see cref="TryCreate"/> の検証を経由させる（設計条件 5「設定ファイルへの
/// 任意文字列注入を作らない」）。
/// </summary>
public sealed class ForwarderKitRequest
{
    private ForwarderKitRequest(string host, int port, IReadOnlyList<string> channels)
    {
        Host = host;
        Port = port;
        Channels = channels;
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
    /// 入力値を検証し、成功時は正規化済みの <see cref="ForwarderKitRequest"/> を返す。
    /// </summary>
    /// <param name="host">宛先ホスト（未指定・空文字はエラー）。</param>
    /// <param name="port">宛先ポート。</param>
    /// <param name="channels">
    /// 収集チャネル（コンマ区切り。前後の空白は許容し正規化時に除去する）。null・空文字は
    /// <see cref="ForwarderKitConstraints.DefaultChannels"/> として扱う。
    /// </param>
    /// <param name="request">検証成功時の結果。</param>
    /// <param name="error">検証失敗時のエラー内容。</param>
    /// <returns>検証に成功したか。</returns>
    public static bool TryCreate(
        string? host,
        int port,
        string? channels,
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

        error = null;
        request = new ForwarderKitRequest(trimmedHost, port, normalizedChannels);
        return true;
    }

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
/// <see cref="ForwarderKitRequest.TryCreate"/> の検証失敗種別（呼び出し側が文言を出し分ける
/// ための分類。エラーメッセージそのものはこのクラスに持たせない——UI 側 <c>UiText</c> /
/// エンドポイント側で言語・文脈ごとに組み立てる）。
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
}
