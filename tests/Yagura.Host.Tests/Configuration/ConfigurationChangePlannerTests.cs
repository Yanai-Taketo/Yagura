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
/// <remarks>
/// <b>網羅性テストについて（Issue #191）</b>: <c>YaguraConfigurationOptionsClonerTests</c>
/// や <c>RetentionTests</c> の <c>KnownKeys</c> ⇔ <see cref="ConfigurationKeyMetadata"/> 双方向
/// 同期テストと同種の「<see cref="ConfigurationChangePlanner.Compare"/> が
/// <see cref="ConfigurationKeyMetadata.RegisteredKeys"/> を全て比較している」ことを機械検証する
/// テストの追加を検討したが、本 Issue の対応時点で <c>Compare</c> は
/// <c>Ingestion:Udp:ReceiveBufferBytes</c>・<c>Ingestion:Tcp:BindAddress</c>・
/// <c>Ingestion:Tcp:Port</c>・<c>Retention:Days</c>・<c>Retention:ExecutionTimeOfDay</c> の
/// 比較も欠いており（<see cref="ConfigurationChangePlanner"/> のクラスコメント参照）、厳密な
/// 網羅性テストを追加すると本 Issue のスコープ外のキーまで一度に修正する必要が生じる。
/// そのため本 Issue では個別キー（<c>Viewer:ReverseDns:Enabled</c>）の回帰テストのみを追加し、
/// 網羅性テストは残ギャップの解消時（別 Issue）に導入する。
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

    // ------------------------------------------------------------------
    // Viewer:ReverseDns:Enabled の比較漏れ回帰テスト（Issue #191。PR #190 の調査で
    // 発見・記録された既知ギャップの修正確認）。
    // ------------------------------------------------------------------

    [Fact]
    public void Compare_ReverseDnsEnabledChanged_NullToConfigured_DetectsChangeAsImmediate()
    {
        // 手編集で ReverseDns:Enabled = false を設定した状態から、ウィザードが Viewer の
        // 他フィールドだけを操作した場合でも、Cloner（#186/PR #190 で修正済み）が値を保持し、
        // Compare がその変更（未設定→設定）を検出できることを確認する。
        var before = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "8514" },
        };
        var after = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions
            {
                HttpPort = "8514",
                ReverseDns = new YaguraConfigurationOptions.ViewerOptions.ReverseDnsOptions { Enabled = "false" },
            },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(new[] { "Viewer:ReverseDns:Enabled" }, plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.Immediate, plan.RequiredEffect);
    }

    [Fact]
    public void Compare_ReverseDnsEnabledChanged_TrueToFalse_DetectsChange()
    {
        var before = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions
            {
                ReverseDns = new YaguraConfigurationOptions.ViewerOptions.ReverseDnsOptions { Enabled = "true" },
            },
        };
        var after = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions
            {
                ReverseDns = new YaguraConfigurationOptions.ViewerOptions.ReverseDnsOptions { Enabled = "false" },
            },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(new[] { "Viewer:ReverseDns:Enabled" }, plan.ChangedKeys);
    }

    [Fact]
    public void Compare_ReverseDnsEnabledUnchanged_NotDetectedAsChange()
    {
        var before = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions
            {
                ReverseDns = new YaguraConfigurationOptions.ViewerOptions.ReverseDnsOptions { Enabled = "true" },
            },
        };
        var after = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions
            {
                ReverseDns = new YaguraConfigurationOptions.ViewerOptions.ReverseDnsOptions { Enabled = "true" },
            },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.False(plan.HasChanges);
        Assert.Empty(plan.ChangedKeys);
    }

    [Theory]
    [InlineData("Ingestion:Udp:BindAddress", ConfigurationReloadEffect.ListenerReconfiguration)]
    [InlineData("Ingestion:Udp:Port", ConfigurationReloadEffect.ListenerReconfiguration)]
    [InlineData("Ingestion:Udp:ReceiveBufferBytes", ConfigurationReloadEffect.ListenerReconfiguration)]
    [InlineData("Viewer:HttpPort", ConfigurationReloadEffect.RestartRequired)]
    [InlineData("Viewer:ReverseDns:Enabled", ConfigurationReloadEffect.Immediate)]
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
