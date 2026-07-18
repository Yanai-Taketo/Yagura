using Microsoft.Extensions.Configuration;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <c>yagura.json</c> の 2 つの読み手が同じ受理範囲を持つことの回帰テスト
/// （configuration.md §1 の不変条件。Issue #312）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜこのテストが要るか</b>: 設定ファイルの読み手は 2 つある——実効値を作る
/// <see cref="YaguraConfigurationLoader"/>（<c>AddJsonFile</c> + <c>Bind</c>）と、
/// 差分計算の基準を取る <see cref="YaguraConfigurationWriter.Read(string)"/>
/// （<c>JsonSerializer</c>）である。片方だけが厳格だと、同じファイルで起動できたり
/// できなかったりする。実際に後者が後から追加された際、手編集で数値を書いた設定ファイルが
/// サービスを起動不能にする退行が生じた（CF-4 実装で混入。#309 の調査で表面化）。
/// </para>
/// <para>
/// <b>読み手を足した人が気づけるようにする</b>のが本テストの目的である。片方の受理範囲を
/// 変えると、もう片方との差がここで落ちる。基準は .NET 構成システム側であり、
/// <see cref="YaguraConfigurationWriter"/> がそれに合わせる（§1）。
/// </para>
/// </remarks>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class ConfigurationReaderLeniencyTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-leniency-test-{Guid.NewGuid():N}");

    public ConfigurationReaderLeniencyTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private void WriteConfiguration(string json) =>
        File.WriteAllText(Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName), json);

    /// <summary>
    /// UTF-8 BOM 付きで設定ファイルを書く。Windows PowerShell 5.1 の
    /// <c>Set-Content -Encoding utf8</c> や一部のエディタが既定で行う保存形式を再現する（Issue #344）。
    /// </summary>
    private void WriteConfigurationWithUtf8Bom(string json) =>
        File.WriteAllBytes(
            Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName),
            [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. System.Text.Encoding.UTF8.GetBytes(json)]);

    /// <summary>
    /// 構成システム側（基準）が読み取った値。<see cref="YaguraConfigurationLoader"/> と同じ経路を使う。
    /// </summary>
    private string? ReadViaConfigurationSystem(string key)
    {
        var root = new ConfigurationBuilder()
            .SetBasePath(_dataRoot)
            .AddJsonFile(YaguraConfigurationLoader.ConfigurationFileName, optional: false, reloadOnChange: false)
            .Build();

        return root[key];
    }

    // ------------------------------------------------------------------
    // スカラーの型不一致（本件の退行そのもの）
    // ------------------------------------------------------------------

    /// <summary>
    /// 手編集で数値・真偽値を素直に書いた場合。構成システムは元から受理しており、
    /// <see cref="YaguraConfigurationWriter"/> もこれに合わせる（§1）。
    /// </summary>
    [Theory]
    // 数値は表記のまま保つ（514.0 を "514" へ正規化しない）
    [InlineData("""{ "Spool": { "QuotaBytes": 4194304 } }""", "Spool:QuotaBytes", "4194304")]
    [InlineData("""{ "Spool": { "QuotaBytes": 514.0 } }""", "Spool:QuotaBytes", "514.0")]
    // 真偽値は "True"/"False"（先頭大文字）になる——構成システムの実測挙動
    [InlineData("""{ "Spool": { "Enabled": true } }""", "Spool:Enabled", "True")]
    [InlineData("""{ "Spool": { "Enabled": false } }""", "Spool:Enabled", "False")]
    // 文字列はそのまま
    [InlineData("""{ "Spool": { "QuotaBytes": "4194304" } }""", "Spool:QuotaBytes", "4194304")]
    // 空文字と空配列は区別できない（§1 はいずれも「使える値がない」として扱うと定める）
    [InlineData("""{ "Spool": { "QuotaBytes": "" } }""", "Spool:QuotaBytes", "")]
    [InlineData("""{ "Spool": { "QuotaBytes": [] } }""", "Spool:QuotaBytes", "")]
    public void BothReaders_AgreeOnScalarValue(string json, string key, string expected)
    {
        WriteConfiguration(json);

        var viaConfigurationSystem = ReadViaConfigurationSystem(key);
        var viaWriter = ReadSpoolValue(key);

        Assert.Equal(expected, viaConfigurationSystem);
        Assert.Equal(expected, viaWriter);
    }

    /// <summary>
    /// 値が取れない場合。<c>null</c> と空オブジェクトはキーの欠落と区別できない（§1 が明示的に受け入れた限界）。
    /// </summary>
    [Theory]
    [InlineData("""{ "Spool": { "QuotaBytes": null } }""", "Spool:QuotaBytes")]
    [InlineData("""{ "Spool": { "QuotaBytes": {} } }""", "Spool:QuotaBytes")]
    // 中身のある構造は子キーへ展開されるため、親キー自体は値を持たない
    [InlineData("""{ "Spool": { "QuotaBytes": { "a": 1 } } }""", "Spool:QuotaBytes")]
    public void BothReaders_AgreeOnAbsentValue(string json, string key)
    {
        WriteConfiguration(json);

        Assert.Null(ReadViaConfigurationSystem(key));
        Assert.Null(ReadSpoolValue(key));
    }

    // ------------------------------------------------------------------
    // ファイル全体の受理範囲（末尾カンマ・コメント・重複キー）
    // ------------------------------------------------------------------

    /// <summary>
    /// 構成システムが受理する記法は <see cref="YaguraConfigurationWriter"/> も受理しなければならない。
    /// これらは手編集で普通に書かれうる（コメントで設定意図を残す等）。
    /// </summary>
    [Theory]
    [InlineData("""{ "Spool": { "QuotaBytes": "4194304", } }""")] // 末尾カンマ
    [InlineData("""
        {
          // スプール上限（ベンチ用に小さくしている）
          "Spool": { "QuotaBytes": "4194304" }
        }
        """)] // 行コメント
    [InlineData("""{ "Spool": { /* 上限 */ "QuotaBytes": "4194304" } }""")] // ブロックコメント
    public void BothReaders_AcceptLenientJsonSyntax(string json)
    {
        WriteConfiguration(json);

        Assert.Equal("4194304", ReadViaConfigurationSystem("Spool:QuotaBytes"));
        Assert.Equal("4194304", ReadSpoolValue("Spool:QuotaBytes"));
    }

    /// <summary>
    /// 同一キーの重複定義は、どちらを採るかを利用者の意図として復元できないため受理しない（§1）。
    /// 構成システムは元から拒否する（後勝ちで黙って通さない）ので、書き手側もそれに合わせる。
    /// </summary>
    [Fact]
    public void BothReaders_RejectDuplicateKeys()
    {
        WriteConfiguration("""{ "Spool": { "QuotaBytes": "1", "QuotaBytes": "2" } }""");

        Assert.ThrowsAny<Exception>(() => ReadViaConfigurationSystem("Spool:QuotaBytes"));
        Assert.ThrowsAny<Exception>(() => YaguraConfigurationWriter.Read(_dataRoot));
    }

    /// <summary>
    /// 本当に壊れた JSON は、どちらの読み手も受理しない（§1 の「読み取り・解析の失敗」= 起動失敗）。
    /// 受理範囲を広げすぎていないことの確認。
    /// </summary>
    [Fact]
    public void BothReaders_RejectMalformedJson()
    {
        WriteConfiguration("""{ "Spool": { "QuotaBytes": "4194304" """); // 閉じ括弧なし

        Assert.ThrowsAny<Exception>(() => ReadViaConfigurationSystem("Spool:QuotaBytes"));
        Assert.ThrowsAny<Exception>(() => YaguraConfigurationWriter.Read(_dataRoot));
    }

    // ------------------------------------------------------------------
    // UTF-8 BOM（Issue #344）
    // ------------------------------------------------------------------

    /// <summary>
    /// UTF-8 BOM 付きの設定ファイルを両方の読み手が同じように受理する。
    /// </summary>
    /// <remarks>
    /// BOM は Windows PowerShell 5.1 の <c>Set-Content -Encoding utf8</c> が既定で付与するため、
    /// 手編集による設定変更で普通に踏み得る。構成システム（<c>AddJsonFile</c>）は元から BOM を
    /// 許容しており、<see cref="YaguraConfigurationWriter"/> 側だけが
    /// <c>'0xEF' is an invalid start of a value</c> で失敗してサービスを起動不能にしていた。
    /// </remarks>
    [Theory]
    [InlineData("""{ "Spool": { "QuotaBytes": "4194304" } }""")]
    [InlineData("""{ "Spool": { "QuotaBytes": 4194304 } }""")] // BOM と型不一致の組み合わせ
    public void BothReaders_AcceptUtf8Bom(string json)
    {
        WriteConfigurationWithUtf8Bom(json);

        Assert.Equal("4194304", ReadViaConfigurationSystem("Spool:QuotaBytes"));
        Assert.Equal("4194304", ReadSpoolValue("Spool:QuotaBytes"));
    }

    /// <summary>
    /// BOM の有無で <see cref="ConfigurationVersionToken"/> は変わる（ディスク上の内容が異なるため）。
    /// </summary>
    /// <remarks>
    /// トークンは BOM を除去する<b>前</b>のバイト列から計算しなければならない。
    /// <see cref="ConfigurationVersionToken.FromFile(string)"/> は生のファイル内容をハッシュしており、
    /// <see cref="YaguraConfigurationWriter.Read(string)"/> が除去後のバイト列を使うと BOM 付き
    /// ファイルで両者が食い違い、保存時の楽観的競合検出が誤検知する。
    /// </remarks>
    [Fact]
    public void ReadToken_MatchesFromFile_EvenWithUtf8Bom()
    {
        const string Json = """{ "Spool": { "QuotaBytes": "4194304" } }""";
        WriteConfigurationWithUtf8Bom(Json);
        var path = Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName);

        var viaRead = YaguraConfigurationWriter.Read(_dataRoot).VersionToken;
        var viaFile = ConfigurationVersionToken.FromFile(path);

        Assert.Equal(viaFile, viaRead);

        // BOM なしの同内容とはトークンが異なる（内容が実際に違うため）
        WriteConfiguration(Json);
        Assert.NotEqual(viaRead, YaguraConfigurationWriter.Read(_dataRoot).VersionToken);
    }

    // ------------------------------------------------------------------
    // 起動経路の回帰（本件の退行が再発しないこと）
    // ------------------------------------------------------------------

    /// <summary>
    /// 起動時は <see cref="YaguraConfigurationWriter.Read(string)"/> が差分計算の基準を取るために
    /// 呼ばれる（Program.cs）。ここで例外が出るとサービスが起動できない——これが #312 の退行だった。
    /// </summary>
    [Fact]
    public void WriterRead_WithHandEditedNumericValue_DoesNotThrow()
    {
        WriteConfiguration("""{ "Spool": { "QuotaBytes": 4194304 } }""");

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);

        Assert.Equal("4194304", snapshot.Options.Spool?.QuotaBytes);
    }

    private string? ReadSpoolValue(string key)
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);

        return key switch
        {
            "Spool:QuotaBytes" => snapshot.Options.Spool?.QuotaBytes,
            "Spool:Enabled" => snapshot.Options.Spool?.Enabled,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "テストが想定していないキーです。"),
        };
    }
}
