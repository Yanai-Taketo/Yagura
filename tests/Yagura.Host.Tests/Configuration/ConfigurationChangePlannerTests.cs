using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <see cref="ConfigurationChangePlanner"/> の単体テスト（M3-3）。
/// </summary>
/// <remarks>
/// configuration.md §3「変更前後の設定を比較して、必要な反映アクションの最大
/// （即時 &lt; リスナ再構成 &lt; 再起動）を返す」を検証する。単一キー変更・複数キー変更の
/// それぞれで最大値が正しく返ること、変更なしでは <see cref="ConfigurationReloadEffect.Immediate"/>
/// が返ることを確認する。
/// </remarks>
public sealed class ConfigurationChangePlannerTests
{
    [Fact]
    public void Compare_NoChanges_ReturnsImmediateAndNoChangedKeys()
    {
        var before = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "8514" },
        };
        var after = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "8514" },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.False(plan.HasChanges);
        Assert.Empty(plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.Immediate, plan.RequiredEffect);
    }

    [Fact]
    public void Compare_SingleKeyChanged_ListenerReconfiguration_ReturnsThatEffect()
    {
        var before = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Udp = new YaguraConfigurationOptions.IngestionOptions.UdpOptions { Port = "514" },
            },
        };
        var after = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Udp = new YaguraConfigurationOptions.IngestionOptions.UdpOptions { Port = "5140" },
            },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(new[] { "Ingestion:Udp:Port" }, plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.ListenerReconfiguration, plan.RequiredEffect);
    }

    [Fact]
    public void Compare_SingleKeyChanged_RestartRequired_ReturnsThatEffect()
    {
        var before = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "8514" },
        };
        var after = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9100" },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(new[] { "Viewer:HttpPort" }, plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.RestartRequired, plan.RequiredEffect);
    }

    [Fact]
    public void Compare_MultipleKeysChanged_ReturnsMaximumEffect()
    {
        // Ingestion:Udp:Port（リスナ再構成）と Viewer:HttpPort（再起動）の両方を変更 →
        // 最大は再起動。
        var before = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Udp = new YaguraConfigurationOptions.IngestionOptions.UdpOptions { Port = "514" },
            },
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "8514" },
        };
        var after = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Udp = new YaguraConfigurationOptions.IngestionOptions.UdpOptions { Port = "5140" },
            },
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9100" },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(2, plan.ChangedKeys.Count);
        Assert.Contains("Ingestion:Udp:Port", plan.ChangedKeys);
        Assert.Contains("Viewer:HttpPort", plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.RestartRequired, plan.RequiredEffect);
    }

    [Fact]
    public void Compare_AllFourKeysChanged_ReturnsAllChangedKeysAndMaximumEffect()
    {
        var before = new YaguraConfigurationOptions();
        var after = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Udp = new YaguraConfigurationOptions.IngestionOptions.UdpOptions
                {
                    BindAddress = "192.168.1.10",
                    Port = "5140",
                },
            },
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9100" },
            Storage = new YaguraConfigurationOptions.StorageOptions { SqliteFileName = "custom.db" },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(4, plan.ChangedKeys.Count);
        Assert.Equal(ConfigurationReloadEffect.RestartRequired, plan.RequiredEffect);
    }

    [Theory]
    [InlineData("Ingestion:Udp:BindAddress", ConfigurationReloadEffect.ListenerReconfiguration)]
    [InlineData("Ingestion:Udp:Port", ConfigurationReloadEffect.ListenerReconfiguration)]
    [InlineData("Viewer:HttpPort", ConfigurationReloadEffect.RestartRequired)]
    [InlineData("Storage:SqliteFileName", ConfigurationReloadEffect.RestartRequired)]
    public void GetReloadEffect_KnownKeys_ReturnsDeclaredEffect(string key, ConfigurationReloadEffect expected)
    {
        Assert.Equal(expected, ConfigurationKeyMetadata.GetReloadEffect(key));
    }

    [Fact]
    public void GetReloadEffect_UnknownKey_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => ConfigurationKeyMetadata.GetReloadEffect("Nonexistent:Key"));
    }
}
