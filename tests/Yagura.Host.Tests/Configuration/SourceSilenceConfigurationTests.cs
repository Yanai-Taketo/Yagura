using System.Text.Json;
using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Configuration;
using Yagura.Host.Observability.ActiveNotification.SourceSilence;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// 送信元の途絶検知（ADR-0018。opt-in・既定無効。Issue #351 第 1 段）の設定解決
/// （<c>Notification:SourceSilence:*</c>）の単体テスト。
/// </summary>
/// <remarks>
/// 本クラスが守る不変条件は 3 つ:
/// (1) 不正なエントリが<b>他のエントリの監視を巻き添えにしない</b>（決定 1 のエントリ単位縮退）、
/// (2) <b>黙って監視対象から外れない</b>（外したエントリは必ず警告に現れる——「監視されている
/// つもりで監視されていない」検知ギャップを作らない）、
/// (3) 空リストは正常な無効化であり警告しない（§1 の「空配列 = 不正値」規定の例外）。
/// </remarks>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class SourceSilenceConfigurationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-silence-config-{Guid.NewGuid():N}");

    public SourceSilenceConfigurationTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private ConfigurationLoadResult Load(string? json = null)
    {
        if (json is not null)
        {
            File.WriteAllText(Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName), json);
        }

        return YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());
    }

    private static string Watchlist(string entriesJson, string? defaultThreshold = null)
    {
        var defaultLine = defaultThreshold is null
            ? string.Empty
            : $"""      "DefaultThresholdMinutes": "{defaultThreshold}",{Environment.NewLine}""";

        return $$"""
        {
          "Notification": {
            "SourceSilence": {
        {{defaultLine}}      "Watchlist": {{entriesJson}}
            }
          }
        }
        """;
    }

    // ------------------------------------------------------------------
    // 無効・空（機能無効。警告なし）
    // ------------------------------------------------------------------

    [Fact]
    public void Load_ConfigurationFileMissing_SourceSilenceIsNull()
    {
        var result = Load();

        Assert.Null(result.Configuration.SourceSilence);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.UnknownKeys);
    }

    [Fact]
    public void Load_EmptyWatchlist_IsANormalDisabledStateWithoutWarning()
    {
        // ADR-0018 決定 1: 空配列は configuration.md §1 の「空配列 = 不正値」規定の例外。
        // 「空 + 機能有効 = 誰も対象にならない」文脈の規定であり、本キーは空 = 意図的な無効。
        var result = Load(Watchlist("[]"));

        Assert.Null(result.Configuration.SourceSilence);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.UnknownKeys);
    }

    // ------------------------------------------------------------------
    // 正常な解決
    // ------------------------------------------------------------------

    [Fact]
    public void Load_ValidEntries_AreResolvedWithEffectiveThresholds()
    {
        var result = Load(Watchlist("""
            [
              { "Address": "192.0.2.10", "Label": "支店ルータ", "ThresholdMinutes": "30" },
              { "Address": "192.0.2.11" }
            ]
            """));

        var resolved = Assert.IsType<ResolvedSourceSilence>(result.Configuration.SourceSilence);
        Assert.Equal(2, resolved.Watchlist.Count);

        Assert.Equal("192.0.2.10", resolved.Watchlist[0].Address.ToString());
        Assert.Equal("支店ルータ", resolved.Watchlist[0].Label);
        Assert.Equal(TimeSpan.FromMinutes(30), resolved.Watchlist[0].Threshold);
        Assert.False(resolved.Watchlist[0].ThresholdIsDefaulted);

        // 閾値省略は既定値で補完し、補完であることを識別可能にする（決定 1）。
        Assert.Equal(TimeSpan.FromMinutes(SourceSilenceConstants.DefaultThresholdMinutes), resolved.Watchlist[1].Threshold);
        Assert.True(resolved.Watchlist[1].ThresholdIsDefaulted);
        Assert.Single(resolved.DefaultedEntries);

        // 閾値の省略自体は正当な使い方であり、警告ではない（情報ログで残す）。
        Assert.Empty(result.Warnings);
        Assert.Empty(result.UnknownKeys);
    }

    [Fact]
    public void Load_DefaultThresholdKey_OverridesTheBuiltInDefault()
    {
        var result = Load(Watchlist("""[ { "Address": "192.0.2.10" } ]""", defaultThreshold: "60"));

        Assert.Equal(TimeSpan.FromMinutes(60), result.Configuration.SourceSilence!.Watchlist[0].Threshold);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_IPv4MappedIPv6_IsNormalizedToIPv4()
    {
        // 流量制御・Top talkers と同じ既存規約。同一装置が 2 エントリに割れ、片方だけが
        // 更新されて他方が途絶に見える事故を防ぐ。
        var result = Load(Watchlist("""[ { "Address": "::ffff:192.0.2.10" } ]"""));

        Assert.Equal("192.0.2.10", result.Configuration.SourceSilence!.Watchlist[0].Address.ToString());
    }

    // ------------------------------------------------------------------
    // エントリ単位の縮退（決定 1。§1 の 3 分類に対する第 4 の挙動）
    // ------------------------------------------------------------------

    [Fact]
    public void Load_InvalidAddress_DisablesOnlyThatEntry()
    {
        var result = Load(Watchlist("""
            [
              { "Address": "192.0.2.10" },
              { "Address": "これはIPではない" },
              { "Address": "192.0.2.12" }
            ]
            """));

        // 1 エントリのタイポで他の監視まで止めない。
        var resolved = result.Configuration.SourceSilence!;
        Assert.Equal(2, resolved.Watchlist.Count);
        Assert.DoesNotContain(resolved.Watchlist, e => e.Address.ToString() == "これはIPではない");

        // ただし黙って外さない。
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("Address", warning.Key);
        Assert.Contains("これはIPではない", warning.InvalidValue);
    }

    [Theory]
    [InlineData("9")]      // 下限 10 未満
    [InlineData("43201")]  // 上限 43200 超
    [InlineData("すぐ")]   // 整数でない
    public void Load_ThresholdOutOfRange_DisablesOnlyThatEntry_WithoutFallingBackToDefault(string threshold)
    {
        var result = Load(Watchlist($$"""
            [
              { "Address": "192.0.2.10" },
              { "Address": "192.0.2.11", "ThresholdMinutes": "{{threshold}}" }
            ]
            """));

        // 既定値へ黙って倒すと「指定したつもりの閾値で監視されていない」状態になる。
        var resolved = result.Configuration.SourceSilence!;
        Assert.Single(resolved.Watchlist);
        Assert.Equal("192.0.2.10", resolved.Watchlist[0].Address.ToString());

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("ThresholdMinutes", warning.Key);
    }

    [Fact]
    public void Load_DuplicateAddress_KeepsTheFirstAndWarns()
    {
        var result = Load(Watchlist("""
            [
              { "Address": "192.0.2.10", "ThresholdMinutes": "30" },
              { "Address": "192.0.2.10", "ThresholdMinutes": "60" }
            ]
            """));

        var resolved = result.Configuration.SourceSilence!;
        Assert.Single(resolved.Watchlist);
        Assert.Equal(TimeSpan.FromMinutes(30), resolved.Watchlist[0].Threshold);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Load_AllEntriesInvalid_YieldsNullButStillWarns()
    {
        var result = Load(Watchlist("""[ { "Address": "だめ" } ]"""));

        Assert.Null(result.Configuration.SourceSilence);
        Assert.NotEmpty(result.Warnings);
    }

    // ------------------------------------------------------------------
    // 上限（決定 1。ファイル順で先頭から採用し、超過分を列挙して警告）
    // ------------------------------------------------------------------

    [Fact]
    public void Load_BeyondTheCap_KeepsFileOrderPrefix_AndListsWhatWasDropped()
    {
        // アドレスの導出はテストと生成で同じ式を使う（手計算するとテスト側の算術ミスを
        // 実装の不具合と取り違える——実際に一度やった）。
        static string AddressAt(int i) => $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}";

        var cap = SourceSilenceConstants.MaxWatchlistEntries;
        var total = cap + 3;
        var entries = string.Join(",", Enumerable.Range(0, total)
            .Select(i => $$"""{ "Address": "{{AddressAt(i)}}" }"""));

        var result = Load(Watchlist($"[{entries}]"));

        var resolved = result.Configuration.SourceSilence!;
        Assert.Equal(cap, resolved.Watchlist.Count);
        // ファイル順で先頭から採用する。
        Assert.Equal(AddressAt(0), resolved.Watchlist[0].Address.ToString());
        Assert.Equal(AddressAt(cap - 1), resolved.Watchlist[^1].Address.ToString());

        // 「監視されているつもりで監視されていない」を黙らせない——外したアドレスを列挙する。
        var warning = Assert.Single(result.Warnings, w => w.Key == "Notification:SourceSilence:Watchlist");
        for (var i = cap; i < total; i++)
        {
            Assert.Contains(AddressAt(i), warning.Reason);
        }

        // 先頭への追記が末尾の既存監視を押し出す向きであることを運用者へ伝える（申し送り D-2）。
        Assert.Contains("末尾", warning.Reason);
    }

    // ------------------------------------------------------------------
    // 未知キー検出（オブジェクト構造化配列の平坦化。ADR-0018 委任 3）
    // ------------------------------------------------------------------

    [Fact]
    public void Load_MisspelledEntryField_IsReportedAsUnknownKey()
    {
        // フィールド名まで照合するため、綴り間違いは未知キーとして現れる。これは意図した挙動
        // ——`Adress` と書いたエントリが黙って無視される（＝監視されているつもりで監視されて
        // いない）のを防ぐ。
        var result = Load(Watchlist("""[ { "Address": "192.0.2.10", "Adress": "192.0.2.11" } ]"""));

        Assert.Contains("Notification:SourceSilence:Watchlist:0:Adress", result.UnknownKeys);
    }

    /// <summary>
    /// 配列キーに空文字 <c>""</c> を書いた場合の挙動を固定する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>実測（2026-07-19）で判明した非対称</b>: .NET 構成システム上、空配列 <c>[]</c> と
    /// 空文字 <c>""</c> は<b>どちらもリーフ値 <c>""</c> として現れ区別できない</b>
    /// （ADR-0018 委任 3 の指摘どおり）。一方 <c>System.Text.Json</c>（<c>YaguraConfigurationWriter</c>）は
    /// <c>""</c> を <c>List&lt;T&gt;</c> へ変換できず <c>JsonException</c> を投げる。
    /// つまり <b><c>YaguraConfigurationLoader</c> は受理し <c>YaguraConfigurationWriter</c> は拒否する</b>。
    /// </para>
    /// <para>
    /// <b>この非対称は解消できない</b>——loader 側で拒否しようにも、区別できない <c>[]</c>
    /// （＝正常な空リスト。ADR-0018 決定 1）まで巻き添えにしてしまう。writer 側で受理させるのは、
    /// 打ち間違いを黙って「空リスト」に化けさせることになり、より悪い。
    /// </para>
    /// <para>
    /// <b>ただし利用者から見た挙動は「必ず・大きく失敗する」に揃っている</b>。起動経路は
    /// <c>Program.cs</c> が <c>YaguraConfigurationWriter.Read</c> を先に呼ぶため起動失敗（1024）、
    /// 再読み込み経路は <c>ConfigurationReloadService</c> が <c>JsonException</c> を捕捉して
    /// 適用拒否（1021）。**「再読み込みは通ったのに再起動で起動しない」という #312 の潜伏事故には
    /// ならない**（潜伏の向きが逆——writer が厳しい側であり、writer は両経路に居る）。
    /// </para>
    /// <para>
    /// 本テストはこの整理を機械的に固定する。将来 writer 側が <c>""</c> を受理するように
    /// 変わった場合（＝打ち間違いが黙って空リストになる退行）を検出する。
    /// なお本性質は本 ADR 固有ではなく、既存の <c>*Groups</c> 配列キーも同じである。
    /// </para>
    /// </remarks>
    [Fact]
    public void Load_EmptyStringInsteadOfArray_FailsLoudlyThroughTheWriter()
    {
        var json = Watchlist("\"\"");
        File.WriteAllText(Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName), json);

        // 構成システム側は受理する（[] と区別できないため）。
        Assert.Null(Record.Exception(() => YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger())));

        // 起動・再読み込みの両経路が通る writer は拒否する——ここが利用者に見える挙動を決める。
        Assert.IsAssignableFrom<JsonException>(
            Record.Exception(() => YaguraConfigurationWriter.Read(_dataRoot)));
    }

    [Fact]
    public void Load_WellFormedEntries_ProduceNoUnknownKeys()
    {
        var result = Load(Watchlist("""
            [ { "Address": "192.0.2.10", "Label": "ルータ", "ThresholdMinutes": "30" } ]
            """));

        Assert.Empty(result.UnknownKeys);
    }
}
