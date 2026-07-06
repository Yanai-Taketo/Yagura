using System.IO.Compression;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// <see cref="ForwarderKitBuilder"/> の ZIP 組み立てテスト（ADR-0008 設計条件 3・7・8）。
/// </summary>
public sealed class ForwarderKitBuilderTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 7, 7, 9, 30, 0, TimeSpan.FromHours(9));

    [Fact]
    public void Build_ContainsAllSixEntries()
    {
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(
            new[]
            {
                "GENERATED.txt",
                "README.md",
                "fluent-bit-yagura.conf",
                "install.ps1",
                "uninstall.ps1",
                "winevt-severity.lua",
            }.OrderBy(n => n, StringComparer.Ordinal),
            names);
    }

    [Fact]
    public void Build_ConfEntry_HasSubstitutedDestinationAndChannels()
    {
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var conf = ReadEntry(archive, "fluent-bit-yagura.conf");

        Assert.Contains("Host                192.0.2.10", conf);
        Assert.Contains("Port                514", conf);
        Assert.Contains("Channels             System,Application", conf);
        Assert.DoesNotContain("@@", conf);
    }

    [Fact]
    public void Build_ReadmeEntry_HasSubstitutedValuesAndNoPlaceholders()
    {
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var readme = ReadEntry(archive, "README.md");

        Assert.Contains("192.0.2.10:514", readme);
        Assert.Contains("System,Application", readme);
        Assert.Contains(ForwarderKitConstraints.VerifiedFluentBitVersion, readme);
        Assert.DoesNotContain("@@", readme);
    }

    [Fact]
    public void Build_GeneratedTxt_ContainsMetadata()
    {
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var generatedTxt = ReadEntry(archive, "GENERATED.txt");

        Assert.Contains("Destination: 192.0.2.10:514/udp", generatedTxt);
        Assert.Contains("Channels: System,Application", generatedTxt);
        Assert.Contains($"Fluent-Bit-Verified: {ForwarderKitConstraints.VerifiedFluentBitVersion}", generatedTxt);
        Assert.Contains("Generated-At:", generatedTxt);
        Assert.Contains("Yagura-Version:", generatedTxt);
    }

    [Fact]
    public void Build_InstallAndUninstallScripts_AreUnmodifiedTemplates()
    {
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var install = ReadEntry(archive, "install.ps1");
        var uninstall = ReadEntry(archive, "uninstall.ps1");

        // 生成キットの conf には @@YAGURA_HOST@@ が残らない(install.ps1 の分岐 = pre-configured)。
        Assert.Contains("YaguraHost", install);
        Assert.Contains("UNINSTALL_SUCCESS", uninstall);
    }

    [Fact]
    public void Build_NoPlaceholdersRemainInAnyTextEntry()
    {
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        foreach (var entryName in new[] { "fluent-bit-yagura.conf", "README.md", "GENERATED.txt" })
        {
            var content = ReadEntry(archive, entryName);
            Assert.DoesNotContain("@@YAGURA_HOST@@", content);
            Assert.DoesNotContain("@@YAGURA_PORT@@", content);
            Assert.DoesNotContain("@@CHANNELS@@", content);
            Assert.DoesNotContain("@@GENERATED_AT@@", content);
            Assert.DoesNotContain("@@FLUENTBIT_VERSION@@", content);
            Assert.DoesNotContain("@@YAGURA_VERSION@@", content);
        }
    }

    private static ForwarderKitRequest CreateRequest()
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System,Application", out var request, out _);
        Assert.True(ok);
        return request!;
    }

    private static ZipArchive OpenArchive(byte[] zipBytes) =>
        new(new MemoryStream(zipBytes), ZipArchiveMode.Read);

    private static string ReadEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"entry not found: {entryName}");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
