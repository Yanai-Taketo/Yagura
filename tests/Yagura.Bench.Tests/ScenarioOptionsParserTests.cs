using Yagura.Bench.LoadGeneration;
using Yagura.Bench.Scenarios;

namespace Yagura.Bench.Tests;

/// <summary>
/// CLI 引数解析（<see cref="ScenarioOptionsParser"/>）の単体テスト。流量制御・drain 待ち上限の
/// オプションを固定し、既存の引数だけを渡した場合の解釈が変わらないことを確かめる。
/// </summary>
/// <remarks>
/// 既定値の不変は CI 回帰ベンチ（ci.yml）の結果の前提である。オプションを足したことで既定の
/// 構成（製品既定の流量制御・drain 待ち 2 分）が黙って変わると、基準値との比較が意味を失う。
/// </remarks>
public sealed class ScenarioOptionsParserTests
{
    [Fact]
    public void Parse_ScenarioOnly_KeepsProductDefaultsForNewOptions()
    {
        var options = ScenarioOptionsParser.Parse(["SustainedZeroDrop"]);

        Assert.Null(options.FlowControlEnabled);
        Assert.Null(options.FlowControlMessagesPerSecond);
        Assert.Null(options.FlowControlBurstSize);
        Assert.Null(options.UdpReceiveBufferBytes);
        Assert.Equal(120, options.DrainTimeoutSeconds);
        Assert.Equal(ScenarioOptions.DefaultDrainTimeoutSeconds, options.DrainTimeoutSeconds);
    }

    /// <summary>ci.yml の回帰ベンチと同じ引数列。解釈が 1 つでも変わると基準値の前提が崩れる。</summary>
    [Fact]
    public void Parse_CiRegressionBenchArguments_AreInterpretedAsBefore()
    {
        var options = ScenarioOptionsParser.Parse(
        [
            "SustainedZeroDrop", "--transport", "udp", "--rate", "5000", "--duration", "10",
            "--output-dir", "bench-results",
            "--compare-baseline", "tools/Yagura.Bench/baselines/ci-baseline.json",
        ]);

        Assert.Equal(BenchScenario.SustainedZeroDrop, options.Scenario);
        Assert.Equal(LoadTransport.Udp, options.Transport);
        Assert.Equal(5000, options.RatePerSecond);
        Assert.Equal(10, options.DurationSeconds);
        Assert.Equal(4, options.SenderSocketCount);
        Assert.Equal(0, options.PaddingBytes);
        Assert.Equal("bench-results", options.OutputDirectory);
        Assert.Equal("tools/Yagura.Bench/baselines/ci-baseline.json", options.CompareBaselinePath);
        Assert.Null(options.FlowControlEnabled);
        Assert.Equal(ScenarioOptions.DefaultDrainTimeoutSeconds, options.DrainTimeoutSeconds);
    }

    [Theory]
    [InlineData("off", false)]
    [InlineData("on", true)]
    [InlineData("OFF", false)]
    public void Parse_FlowControl_SetsEnabled(string value, bool expected)
    {
        var options = ScenarioOptionsParser.Parse(["SustainedZeroDrop", "--flow-control", value]);

        Assert.Equal(expected, options.FlowControlEnabled);
    }

    [Fact]
    public void Parse_FlowControl_InvalidValue_IsUsageError()
    {
        Assert.Throws<BenchUsageException>(
            () => ScenarioOptionsParser.Parse(["SustainedZeroDrop", "--flow-control", "disabled"]));
    }

    [Fact]
    public void Parse_FlowControlThresholds_AreParsed()
    {
        var options = ScenarioOptionsParser.Parse(
            ["BurstQ1Drop", "--flow-control-rate", "50000", "--flow-control-burst", "100000"]);

        Assert.Equal(50000, options.FlowControlMessagesPerSecond);
        Assert.Equal(100000, options.FlowControlBurstSize);
        Assert.Null(options.FlowControlEnabled);
    }

    /// <summary>
    /// 無効にした流量制御へ閾値を渡しても何も起きない。測る側は閾値が効いたと思い込むため、
    /// 黙って受け付けずに使い方エラーにする。
    /// </summary>
    [Theory]
    [InlineData("--flow-control-rate")]
    [InlineData("--flow-control-burst")]
    public void Parse_ThresholdWithFlowControlOff_IsUsageError(string thresholdOption)
    {
        Assert.Throws<BenchUsageException>(
            () => ScenarioOptionsParser.Parse(["SustainedZeroDrop", "--flow-control", "off", thresholdOption, "1000"]));
    }

    [Theory]
    [InlineData("--flow-control-rate", "0")]
    [InlineData("--flow-control-rate", "abc")]
    [InlineData("--flow-control-rate", "100000001")]
    [InlineData("--flow-control-burst", "-1")]
    [InlineData("--drain-timeout", "0")]
    [InlineData("--drain-timeout", "ten")]
    public void Parse_OutOfRangeOrNonNumericValue_IsUsageError(string option, string value)
    {
        Assert.Throws<BenchUsageException>(
            () => ScenarioOptionsParser.Parse(["SustainedZeroDrop", option, value]));
    }

    [Fact]
    public void Parse_DrainTimeout_IsParsed()
    {
        var options = ScenarioOptionsParser.Parse(["ProviderWriteCeiling", "--drain-timeout", "900"]);

        Assert.Equal(900, options.DrainTimeoutSeconds);
    }

    [Fact]
    public void UsageText_ListsNewOptions()
    {
        var usage = ScenarioOptionsParser.BuildUsageText();

        Assert.Contains("--flow-control <on|off>", usage, StringComparison.Ordinal);
        Assert.Contains("--flow-control-rate <N>", usage, StringComparison.Ordinal);
        Assert.Contains("--flow-control-burst <N>", usage, StringComparison.Ordinal);
        Assert.Contains("--drain-timeout <秒>", usage, StringComparison.Ordinal);
    }
}
