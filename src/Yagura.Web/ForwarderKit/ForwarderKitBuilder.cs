using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Yagura.Web.ForwarderKit;

/// <summary>
/// フォワーダ配布キット（ZIP）の組み立て（ADR-0008 設計条件 3・4・7・8）。
/// </summary>
/// <remarks>
/// <para>
/// テンプレートはビルド時に埋め込んだリソース（<c>Yagura.Web.csproj</c> の
/// <c>EmbeddedResource</c>。<c>forwarder/fluent-bit/</c> を単一ソースとする——ADR-0008 委任 #1）
/// から読み出す。生成は <b>メモリ上の <see cref="MemoryStream"/> 上で完結し、ディスクへ
/// 一時ファイルを書かない</b>（設計条件 7）。外部ネットワークへのアクセスも行わない（設計条件 7）。
/// 生成物に秘密情報は含めない（設計条件 8——入る値は宛先ホスト・ポート・チャネル名のみ）。
/// </para>
/// </remarks>
public static class ForwarderKitBuilder
{
    private const string ResourcePrefix = "Yagura.Web.ForwarderKit.Templates.";

    /// <summary>ZIP 内のファイル名（README は生成テンプレートを README.md として梱包する）。</summary>
    private static class EntryNames
    {
        public const string Conf = "fluent-bit-yagura.conf";
        public const string InstallScript = "install.ps1";
        public const string UninstallScript = "uninstall.ps1";
        public const string LuaFilter = "winevt-severity.lua";
        public const string Readme = "README.md";
        public const string Generated = "GENERATED.txt";
    }

    /// <summary>
    /// 要求内容から ZIP を組み立てる。
    /// </summary>
    /// <param name="request">検証済みの生成要求。</param>
    /// <param name="generatedAt">生成時刻（サーバ現地時刻。オフセット付きで <c>GENERATED.txt</c> に記録する）。</param>
    /// <returns>ZIP アーカイブのバイト列。</returns>
    public static byte[] Build(ForwarderKitRequest request, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(request);

        var confTemplate = ReadTemplate(EntryNames.Conf);
        var installScript = ReadTemplate(EntryNames.InstallScript);
        var uninstallScript = ReadTemplate(EntryNames.UninstallScript);
        var luaFilter = ReadTemplate(EntryNames.LuaFilter);
        var readmeTemplate = ReadTemplate("README.generated.md");

        var conf = SubstituteConf(confTemplate, request);
        var readme = SubstituteReadme(readmeTemplate, request, generatedAt);
        var generatedTxt = BuildGeneratedTxt(request, generatedAt);

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, EntryNames.Conf, conf);
            WriteEntry(archive, EntryNames.InstallScript, installScript);
            WriteEntry(archive, EntryNames.UninstallScript, uninstallScript);
            WriteEntry(archive, EntryNames.LuaFilter, luaFilter);
            WriteEntry(archive, EntryNames.Readme, readme);
            WriteEntry(archive, EntryNames.Generated, generatedTxt);
        }

        return memoryStream.ToArray();
    }

    private static string SubstituteConf(string template, ForwarderKitRequest request) =>
        template
            .Replace("@@YAGURA_HOST@@", request.Host)
            .Replace("@@YAGURA_PORT@@", request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("@@CHANNELS@@", request.ChannelsValue);

    private static string SubstituteReadme(string template, ForwarderKitRequest request, DateTimeOffset generatedAt) =>
        template
            .Replace("@@YAGURA_HOST@@", request.Host)
            .Replace("@@YAGURA_PORT@@", request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("@@CHANNELS@@", request.ChannelsValue)
            .Replace("@@GENERATED_AT@@", FormatTimestamp(generatedAt))
            .Replace("@@FLUENTBIT_VERSION@@", ForwarderKitConstraints.VerifiedFluentBitVersion)
            .Replace("@@YAGURA_VERSION@@", YaguraVersion);

    private static string BuildGeneratedTxt(ForwarderKitRequest request, DateTimeOffset generatedAt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Yagura forwarder kit - generation metadata (ADR-0008)");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Generated-At: {FormatTimestamp(generatedAt)}");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Destination: {request.Host}:{request.Port}/udp");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Channels: {request.ChannelsValue}");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Yagura-Version: {YaguraVersion}");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Fluent-Bit-Verified: {ForwarderKitConstraints.VerifiedFluentBitVersion}");
        return builder.ToString();
    }

    /// <summary>
    /// 生成日時の表記（ISO 8601・オフセット明示。サーバ現地時刻をそのまま出す——
    /// <see cref="DateTimeOffset.ToString(string)"/> の "O" は往復可能かつオフセットを含む）。
    /// </summary>
    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 生成元 Yagura のバージョン（アセンブリ情報バージョン。設定キーを追加せず、
    /// ビルド済みアセンブリから機械的に取得する）。
    /// </summary>
    private static string YaguraVersion =>
        typeof(ForwarderKitBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ForwarderKitBuilder).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string ReadTemplate(string fileName)
    {
        var assembly = typeof(ForwarderKitBuilder).Assembly;
        var resourceName = ResourcePrefix + fileName;

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"埋め込みテンプレートが見つかりません: {resourceName}（Yagura.Web.csproj の EmbeddedResource 設定を確認してください）。");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        // BOM なし UTF-8: install.ps1 の設定書き出しと同じ判断（Fluent Bit のパーサが
        // BOM を想定しない）。README・GENERATED.txt も一貫して BOM なしにする。
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
