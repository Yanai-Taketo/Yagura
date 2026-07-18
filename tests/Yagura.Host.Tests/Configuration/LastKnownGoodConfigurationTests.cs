using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// 良好構成の写し（configuration.md §1）の単体テスト。
/// </summary>
/// <remarks>
/// 保存経路（<see cref="YaguraConfigurationWriter"/>）は原子的置換のみで世代を残さないため、
/// この写しが無いと「直前のファイルに戻してください」という復旧案内が成立しない。
/// </remarks>
public sealed class LastKnownGoodConfigurationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-lkg-test-{Guid.NewGuid():N}");

    public LastKnownGoodConfigurationTests()
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

    private string ConfigurationPath => Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName);

    [Fact]
    public void Save_CopiesCurrentConfiguration()
    {
        File.WriteAllText(ConfigurationPath, """{ "Retention": { "Days": "90" } }""");

        LastKnownGoodConfiguration.Save(_dataRoot);

        var copy = LastKnownGoodConfiguration.GetPath(_dataRoot);
        Assert.True(File.Exists(copy));
        Assert.Equal(File.ReadAllText(ConfigurationPath), File.ReadAllText(copy));
    }

    /// <summary>
    /// 写しは 1 世代のみとし、旧世代を残さない（§1）。手編集で平文の資格情報が書かれた場合、
    /// 世代を溜めると暗号化表現へ移行した後も平文が無期限に残り続けるため。
    /// </summary>
    [Fact]
    public void Save_Twice_KeepsOnlyLatestGeneration()
    {
        File.WriteAllText(ConfigurationPath, """{ "Retention": { "Days": "30" } }""");
        LastKnownGoodConfiguration.Save(_dataRoot);

        File.WriteAllText(ConfigurationPath, """{ "Retention": { "Days": "90" } }""");
        LastKnownGoodConfiguration.Save(_dataRoot);

        var copies = Directory.GetFiles(_dataRoot, "*.last-good*");
        Assert.Single(copies);
        Assert.Contains("90", File.ReadAllText(LastKnownGoodConfiguration.GetPath(_dataRoot)));
    }

    /// <summary>
    /// 設定ファイルが無い状態（ゼロ設定ファーストラン）では写す対象が無い。
    /// 既定値のみで動いている状態に戻すのに写しは要らない。
    /// </summary>
    [Fact]
    public void Save_WithoutConfigurationFile_DoesNothing()
    {
        LastKnownGoodConfiguration.Save(_dataRoot);

        Assert.False(File.Exists(LastKnownGoodConfiguration.GetPath(_dataRoot)));
    }

    /// <summary>
    /// 写しの保存に失敗しても呼び出し側を巻き込まない（§1 の「受信を止めない」優先）。
    /// 写しは復旧の利便のためのものであり、作れないことで起動や再読み込みを止めるのは本末転倒。
    /// </summary>
    [Fact]
    public void Save_WhenDestinationIsLocked_ReportsFailureWithoutThrowing()
    {
        File.WriteAllText(ConfigurationPath, """{ "Retention": { "Days": "90" } }""");

        // 写しの配置先をディレクトリとして作っておくと、ファイルとしては作れない。
        Directory.CreateDirectory(LastKnownGoodConfiguration.GetPath(_dataRoot));

        Exception? reported = null;
        var exception = Record.Exception(() => LastKnownGoodConfiguration.Save(_dataRoot, ex => reported = ex));

        Assert.Null(exception);
        Assert.NotNull(reported);
    }

    /// <summary>
    /// 復旧案内には写しの日時を必ず併記する（§1）。「いつ時点の構成に戻るのか」が分からないまま
    /// 復元させると、復元したのに設定が違うという、起動しないことより気づきにくい事故になる。
    /// </summary>
    [Fact]
    public void BuildRecoveryGuidance_WithCopy_MentionsPathAndTimestamp()
    {
        File.WriteAllText(ConfigurationPath, """{ "Retention": { "Days": "90" } }""");
        LastKnownGoodConfiguration.Save(_dataRoot);

        var guidance = LastKnownGoodConfiguration.BuildRecoveryGuidance(_dataRoot);

        Assert.Contains(LastKnownGoodConfiguration.GetPath(_dataRoot), guidance);
        Assert.Contains(File.GetLastWriteTime(LastKnownGoodConfiguration.GetPath(_dataRoot)).Year.ToString(), guidance);
        Assert.Contains("確認してから", guidance);
    }

    /// <summary>
    /// 写しが無い場合に、存在しない復旧元を案内しない（初版の起案が犯した誤り——
    /// 「直前のファイルへの復元」を案内していたが、製品はそれを保持していなかった）。
    /// </summary>
    [Fact]
    public void BuildRecoveryGuidance_WithoutCopy_DoesNotPromiseRestore()
    {
        var guidance = LastKnownGoodConfiguration.BuildRecoveryGuidance(_dataRoot);

        Assert.Contains("ありません", guidance);
        Assert.DoesNotContain(LastKnownGoodConfiguration.FileName, guidance);
    }
}
