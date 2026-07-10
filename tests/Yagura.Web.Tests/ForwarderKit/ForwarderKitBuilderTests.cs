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
    public void Build_ConfEntry_HasSyslogFacilityKey()
    {
        // Issue #154: facility をチャネル別に付与するため、out_syslog に
        // Syslog_Facility_Key を渡す必要がある(Lua 側が SyslogFacility を record へ設定する)。
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var conf = ReadEntry(archive, "fluent-bit-yagura.conf");

        Assert.Contains("Syslog_Facility_Key SyslogFacility", conf);
    }

    [Fact]
    public void Build_LuaEntry_HandlesAuditFailureKeywordAndChannelFacility()
    {
        // Issue #153 / #154: Lua フィルタが Keywords の Audit Failure ビットと
        // チャネル別 facility を扱っていることを、生成キットに封入された実体で確認する
        // (Lua 自体の実行結果は単体テストの対象外——lab 検証が必要。conventions.md)。
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var lua = ReadEntry(archive, "winevt-severity.lua");

        Assert.Contains("has_audit_failure_keyword", lua);
        Assert.Contains("SyslogFacility", lua);
        Assert.Contains("channel_to_facility", lua);
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

    // ---- BOM 方針（Issue #127: PowerShell 5.1 / 既定コンソールでの文字化け対策） ----

    [Theory]
    [InlineData("fluent-bit-yagura.conf")]
    [InlineData("install.ps1")]
    [InlineData("uninstall.ps1")]
    [InlineData("winevt-severity.lua")]
    public void Build_ProgramParsedEntries_HaveNoUtf8Bom(string entryName)
    {
        // Fluent Bit（conf・Lua）・PowerShell（install.ps1・uninstall.ps1）がパースする
        // 実行時アーティファクトは BOM を付けない（Fluent Bit のパーサが BOM を想定しないため。
        // ForwarderKitBuilder.WriteEntry のコメント参照）。
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var bytes = ReadEntryBytes(archive, entryName);

        Assert.False(StartsWithUtf8Bom(bytes), $"{entryName} に BOM が付与されている");
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("GENERATED.txt")]
    public void Build_HumanReadOnlyEntries_HaveUtf8Bom(string entryName)
    {
        // README・GENERATED.txt は人間が読む専用でどのプログラムもパースしないため、
        // Windows PowerShell 5.1 の既定 Get-Content や既定コードページのコンソールでの
        // 文字化けを防ぐ目的で BOM 付き UTF-8 にする（Issue #127）。
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var bytes = ReadEntryBytes(archive, entryName);

        Assert.True(StartsWithUtf8Bom(bytes), $"{entryName} に BOM が付与されていない");
    }

    // ---- MSI オプトイン同梱（ADR-0008 設計条件 9） ----

    [Fact]
    public void Build_WithoutMsi_FileNameHasNoMsiSuffix()
    {
        var request = CreateRequest();

        var fileName = ForwarderKitBuilder.BuildFileName(request, GeneratedAt);

        Assert.EndsWith("-no-msi.zip", fileName);
    }

    [Fact]
    public void Build_WithMsi_FileNameHasWithMsiSuffix()
    {
        var request = CreateRequestWithMsi(out _);

        var fileName = ForwarderKitBuilder.BuildFileName(request, GeneratedAt);

        Assert.EndsWith("-with-msi.zip", fileName);
    }

    [Fact]
    public void Build_WithMsi_ZipContainsSevenEntriesIncludingMsi()
    {
        var request = CreateRequestWithMsi(out var msiFileName);

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        Assert.Equal(7, archive.Entries.Count);
        Assert.NotNull(archive.GetEntry(msiFileName));
    }

    [Fact]
    public void Build_WithoutMsi_ZipDoesNotContainMsiEntry()
    {
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        Assert.DoesNotContain(archive.Entries, e => e.FullName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_WithMsi_GeneratedTxtContainsMsiMetadataAndKitSha256()
    {
        var request = CreateRequestWithMsi(out var msiFileName);

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var generatedTxt = ReadEntry(archive, "GENERATED.txt");

        Assert.Contains("Msi-Bundled: true", generatedTxt);
        Assert.Contains($"Msi-FileName: {msiFileName}", generatedTxt);
        Assert.Contains("Msi-ProductVersion:", generatedTxt);
        Assert.Contains("Msi-SHA256:", generatedTxt);
        Assert.Contains("Msi-OfficialHashMatch:", generatedTxt);
        Assert.Contains("Kit-SHA256:", generatedTxt);
    }

    [Fact]
    public void Build_WithoutMsi_GeneratedTxtHasMsiBundledFalseAndNoMsiFields()
    {
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var generatedTxt = ReadEntry(archive, "GENERATED.txt");

        Assert.Contains("Msi-Bundled: false", generatedTxt);
        Assert.DoesNotContain("Msi-FileName:", generatedTxt);
        Assert.Contains("Kit-SHA256:", generatedTxt);
    }

    [Fact]
    public void Build_KitSha256_DoesNotDependOnItself()
    {
        // Kit-SHA256 は GENERATED.txt を除く全エントリのハッシュとして定義される
        // （自己参照回避——ADR-0008 設計条件 9）。同一入力からの 2 回のビルドで
        // Kit-SHA256 の値が安定していることを確認する（生成のたびに変わらない）。
        var request = CreateRequestWithMsi(out _);

        var firstBuild = ForwarderKitBuilder.Build(request, GeneratedAt);
        var secondBuild = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var firstArchive = OpenArchive(firstBuild);
        using var secondArchive = OpenArchive(secondBuild);

        var firstKitSha = ExtractKitSha256(ReadEntry(firstArchive, "GENERATED.txt"));
        var secondKitSha = ExtractKitSha256(ReadEntry(secondArchive, "GENERATED.txt"));

        Assert.Equal(firstKitSha, secondKitSha);
    }

    [Fact]
    public void Build_WithMsi_ReadmeEntry_StatesMsiIsBundled()
    {
        var request = CreateRequestWithMsi(out var msiFileName);

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var readme = ReadEntry(archive, "README.md");

        Assert.Contains("同梱済み", readme);
        Assert.Contains(msiFileName, readme);
        Assert.DoesNotContain("@@", readme);
    }

    [Fact]
    public void Build_WithoutMsi_ReadmeEntry_StatesMsiIsNotBundled()
    {
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var readme = ReadEntry(archive, "README.md");

        Assert.Contains("同梱されていません", readme);
        Assert.DoesNotContain("@@", readme);
    }

    private static string ExtractKitSha256(string generatedTxt)
    {
        var line = generatedTxt
            .Split('\n')
            .Select(l => l.Trim())
            .Single(l => l.StartsWith("Kit-SHA256:", StringComparison.Ordinal));
        return line["Kit-SHA256:".Length..].Trim();
    }

    private static ForwarderKitRequest CreateRequest()
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System,Application", out var request, out _);
        Assert.True(ok);
        return request!;
    }

    private static ForwarderKitRequest CreateRequestWithMsi(out string fileName)
    {
        fileName = "fluent-bit-4.0.14-win64.msi";
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{fileName}");
        File.WriteAllBytes(tempPath, [1, 2, 3, 4, 5]);

        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(tempPath)));

        var bundle = new ForwarderMsiBundle(
            FilePath: tempPath,
            FileName: fileName,
            ProductVersion: ForwarderKitConstraints.VerifiedFluentBitVersion,
            Sha256: sha256,
            OfficialHashMatch: OfficialHashMatchResult.Unverified,
            VersionMismatch: false,
            VersionMismatchAcknowledged: false);

        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System,Application", bundle, out var request, out _);
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

    private static byte[] ReadEntryBytes(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"entry not found: {entryName}");
        using var stream = entry.Open();
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static bool StartsWithUtf8Bom(byte[] bytes)
    {
        var preamble = System.Text.Encoding.UTF8.GetPreamble();
        return bytes.Length >= preamble.Length && bytes.AsSpan(0, preamble.Length).SequenceEqual(preamble);
    }

    // ---- 転送方式（Issue #156: UDP/TCP。TLS 送信はキットから除外——オーナー決定 2026-07-11） ----

    [Fact]
    public void Build_ModeUdp_ConfHasNoTlsConfigLinesAndNoPemEntry()
    {
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var conf = ReadEntry(archive, "fluent-bit-yagura.conf");

        // Fluent Bit の設定行（インデント付き "tls" キー）が生成されないこと。
        Assert.DoesNotContain("    tls", conf);
        Assert.DoesNotContain(archive.Entries, e => e.FullName == "yagura-tls-ca.pem");
    }

    [Fact]
    public void Build_ModeUdp_GeneratedTxt_DestinationSlugIsUdp()
    {
        var request = CreateRequest();

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var generatedTxt = ReadEntry(archive, "GENERATED.txt");

        Assert.Contains("Destination: 192.0.2.10:514/udp", generatedTxt);
    }

    [Fact]
    public void Build_ModeTcp_ConfHasModeTcp_AndGeneratedTxtSlugIsTcp()
    {
        var request = CreateRequest(ForwarderKitMode.Tcp);

        var zipBytes = ForwarderKitBuilder.Build(request, GeneratedAt);

        using var archive = OpenArchive(zipBytes);
        var conf = ReadEntry(archive, "fluent-bit-yagura.conf");
        var generatedTxt = ReadEntry(archive, "GENERATED.txt");

        Assert.Contains("Mode                tcp", conf);
        Assert.DoesNotContain("    tls", conf);
        Assert.Contains("Destination: 192.0.2.10:514/tcp", generatedTxt);
    }

    private static ForwarderKitRequest CreateRequest(ForwarderKitMode mode)
    {
        var ok = ForwarderKitRequest.TryCreate(
            "192.0.2.10", 514, "System,Application", msiBundle: null,
            mode, out var request, out _);
        Assert.True(ok);
        return request!;
    }
}
