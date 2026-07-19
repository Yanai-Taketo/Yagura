using System.Reflection;
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
/// <b>網羅性テストについて（Issue #191 / #210）</b>: <c>YaguraConfigurationOptionsClonerTests</c>
/// や <c>RetentionTests</c> の <c>KnownKeys</c> ⇔ <see cref="ConfigurationKeyMetadata"/> 双方向
/// 同期テストと同種の「<see cref="ConfigurationChangePlanner.Compare"/> が
/// <see cref="ConfigurationKeyMetadata.RegisteredKeys"/> を全て比較している」ことを機械検証する
/// テスト（<see cref="Compare_EveryRegisteredKey_ExceptProviderSwitchKeys_IsDetectedAsChanged"/>）
/// は Issue #191 対応時点では PR #209 で試作されたが、当時は <c>Compare</c> が本テストの対象と
/// なる 5 キー（<c>Ingestion:Udp:ReceiveBufferBytes</c>・<c>Ingestion:Tcp:BindAddress</c>・
/// <c>Ingestion:Tcp:Port</c>・<c>Retention:Days</c>・<c>Retention:ExecutionTimeOfDay</c>）の比較を
/// 欠いており即座に失敗するため見送られた。Issue #210 で当該 5 キーの比較漏れを修正したことで
/// 有効化した——以後、新しい設定キーの追加時に本メソッドへの追加を忘れると、このテストが
/// 個別キー名を知らなくても機械的に検出する。
/// </remarks>
public sealed class ConfigurationChangePlannerTests
{
    /// <summary>
    /// <see cref="ConfigurationChangePlanner"/> のクラスコメントに記載の意図的な除外
    /// （database.md §6.1 の専用切替手順が扱う provider 切替キー）。
    /// </summary>
    private static readonly string[] ProviderSwitchKeys =
    [
        "Storage:Provider",
        "Storage:SqlServer:ConnectionString",
    ];
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
    [InlineData("Ingestion:Tcp:BindAddress", ConfigurationReloadEffect.ListenerReconfiguration)]
    [InlineData("Ingestion:Tcp:Port", ConfigurationReloadEffect.ListenerReconfiguration)]
    [InlineData("Ingestion:FlowControl:Enabled", ConfigurationReloadEffect.Immediate)]
    [InlineData("Ingestion:FlowControl:MessagesPerSecond", ConfigurationReloadEffect.Immediate)]
    [InlineData("Ingestion:FlowControl:BurstSize", ConfigurationReloadEffect.Immediate)]
    [InlineData("Viewer:HttpPort", ConfigurationReloadEffect.RestartRequired)]
    [InlineData("Viewer:ReverseDns:Enabled", ConfigurationReloadEffect.Immediate)]
    [InlineData("Admin:Authentication:Windows:Enabled", ConfigurationReloadEffect.RestartRequired)]
    [InlineData("Admin:Authentication:Windows:KerberosOnly", ConfigurationReloadEffect.RestartRequired)]
    [InlineData("Admin:Authentication:App:Enabled", ConfigurationReloadEffect.RestartRequired)]
    [InlineData("Admin:Authentication:RequireForLoopback", ConfigurationReloadEffect.RestartRequired)]
    [InlineData("Storage:SqliteFileName", ConfigurationReloadEffect.RestartRequired)]
    [InlineData("Retention:Days", ConfigurationReloadEffect.Immediate)]
    [InlineData("Retention:ExecutionTimeOfDay", ConfigurationReloadEffect.Immediate)]
    public void GetReloadEffect_KnownKeys_ReturnsDeclaredEffect(string key, ConfigurationReloadEffect expected)
    {
        Assert.Equal(expected, ConfigurationKeyMetadata.GetReloadEffect(key));
    }

    [Fact]
    public void GetReloadEffect_UnknownKey_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => ConfigurationKeyMetadata.GetReloadEffect("Nonexistent:Key"));
    }

    // ------------------------------------------------------------------
    // 配列キーの差分検出（ADR-0017 委任 9。2026-07-19）
    //
    // 従来、配列キーは Compare の対象ですらなく、手編集で宛先・グループ一覧だけを変えて
    // 再読み込みしても「反映もされず再起動待ちにも出ない」無音の穴になっていた。
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Notification:Email:To", ConfigurationReloadEffect.Immediate)]
    [InlineData("Admin:Authentication:Windows:AdminGroups", ConfigurationReloadEffect.RestartRequired)]
    [InlineData("Viewer:Authentication:Windows:ViewerGroups", ConfigurationReloadEffect.RestartRequired)]
    [InlineData("Viewer:Authentication:Windows:AdminGroups", ConfigurationReloadEffect.RestartRequired)]
    public void GetReloadEffect_ArrayKeys_ReturnsDeclaredEffect(string key, ConfigurationReloadEffect expected)
    {
        Assert.Equal(expected, ConfigurationKeyMetadata.GetReloadEffect(key));
    }

    [Fact]
    public void Compare_RecipientListChange_IsDetectedAsImmediate()
    {
        var before = new YaguraConfigurationOptions
        {
            Notification = new YaguraConfigurationOptions.NotificationOptions
            {
                Email = new YaguraConfigurationOptions.NotificationOptions.EmailOptions { To = ["a@example.com"] },
            },
        };
        var after = new YaguraConfigurationOptions
        {
            Notification = new YaguraConfigurationOptions.NotificationOptions
            {
                Email = new YaguraConfigurationOptions.NotificationOptions.EmailOptions
                {
                    To = ["a@example.com", "b@example.com"],
                },
            },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(["Notification:Email:To"], plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.Immediate, plan.RequiredEffect);
    }

    [Fact]
    public void Compare_GroupListChange_IsDetectedAsRestartRequired()
    {
        // グループ一覧は名 → SID 解決を含め反映が起動時に固定されている。検出できないことと
        // 「再起動が要る」と示すことは別であり、後者は示せる（示さないと無音の未反映になる）。
        var before = new YaguraConfigurationOptions();
        var after = new YaguraConfigurationOptions
        {
            Admin = new YaguraConfigurationOptions.AdminOptions
            {
                Authentication = new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions
                {
                    Windows = new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions.WindowsOptions
                    {
                        AdminGroups = ["EXAMPLE\\Admins"],
                    },
                },
            },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(["Admin:Authentication:Windows:AdminGroups"], plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.RestartRequired, plan.RequiredEffect);
    }

    [Fact]
    public void Compare_NullAndEmptyArray_AreTreatedAsTheSame()
    {
        // キーを消した編集と [] に書き換えた編集は、どちらも「1 件も指定がない」。
        // 区別すると再起動待ちの表示に意味のない差が出る。
        var before = new YaguraConfigurationOptions
        {
            Notification = new YaguraConfigurationOptions.NotificationOptions
            {
                Email = new YaguraConfigurationOptions.NotificationOptions.EmailOptions { To = null },
            },
        };
        var after = new YaguraConfigurationOptions
        {
            Notification = new YaguraConfigurationOptions.NotificationOptions
            {
                Email = new YaguraConfigurationOptions.NotificationOptions.EmailOptions { To = [] },
            },
        };

        Assert.Empty(ConfigurationChangePlanner.Compare(before, after).ChangedKeys);
    }

    [Fact]
    public void Compare_ArrayReorder_IsDetectedAsChange()
    {
        // 集合として同じでも設定ファイルは変わっている。「変更したのに出ない」（未反映の
        // 無音化）より「変更していないのに出る」ほうが害が小さい、という優先順位。
        var before = new YaguraConfigurationOptions
        {
            Notification = new YaguraConfigurationOptions.NotificationOptions
            {
                Email = new YaguraConfigurationOptions.NotificationOptions.EmailOptions
                {
                    To = ["a@example.com", "b@example.com"],
                },
            },
        };
        var after = new YaguraConfigurationOptions
        {
            Notification = new YaguraConfigurationOptions.NotificationOptions
            {
                Email = new YaguraConfigurationOptions.NotificationOptions.EmailOptions
                {
                    To = ["b@example.com", "a@example.com"],
                },
            },
        };

        Assert.Equal(["Notification:Email:To"], ConfigurationChangePlanner.Compare(before, after).ChangedKeys);
    }

    [Fact]
    public void Compare_EveryRegisteredArrayKey_IsDetectedAsChanged()
    {
        // スカラー側の網羅性テスト（Compare_EveryRegisteredKey_...）の配列版。
        // 表に登録したのに Compare へ足し忘れる、という組み合わせを CI が検出する。
        foreach (var key in ConfigurationKeyMetadata.RegisteredArrayKeys)
        {
            var before = new YaguraConfigurationOptions();
            var after = new YaguraConfigurationOptions();
            SetArrayByKeyPath(after, key, ["changed-value"]);

            var plan = ConfigurationChangePlanner.Compare(before, after);

            Assert.Equal([key], plan.ChangedKeys);
        }
    }

    /// <summary>
    /// 配列キーのパスを辿って値を設定する（<see cref="SetValueByKeyPath"/> の配列版）。
    /// 末端のプロパティ型は <see cref="List{T}"/> of <see cref="string"/>。
    /// </summary>
    private static void SetArrayByKeyPath(YaguraConfigurationOptions options, string keyPath, List<string> value)
    {
        var segments = keyPath.Split(':');
        object current = options;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var property = current.GetType().GetProperty(segments[i])
                ?? throw new InvalidOperationException(
                    $"キーパス '{keyPath}' の区分 '{segments[i]}' に対応するプロパティがありません。");

            var next = property.GetValue(current);
            if (next is null)
            {
                next = Activator.CreateInstance(property.PropertyType)!;
                property.SetValue(current, next);
            }

            current = next;
        }

        var leaf = current.GetType().GetProperty(segments[^1])
            ?? throw new InvalidOperationException(
                $"キーパス '{keyPath}' の末端 '{segments[^1]}' に対応するプロパティがありません。");

        leaf.SetValue(current, value);
    }

    // ------------------------------------------------------------------
    // 残り 5 キーの比較漏れ回帰テスト（Issue #210。PR #209 の調査で発見・記録された
    // 既知ギャップの修正確認）。
    // ------------------------------------------------------------------

    [Fact]
    public void Compare_UdpReceiveBufferBytesChanged_DetectsChangeAsListenerReconfiguration()
    {
        var before = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Udp = new YaguraConfigurationOptions.IngestionOptions.UdpOptions { ReceiveBufferBytes = "4194304" },
            },
        };
        var after = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Udp = new YaguraConfigurationOptions.IngestionOptions.UdpOptions { ReceiveBufferBytes = "8388608" },
            },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(new[] { "Ingestion:Udp:ReceiveBufferBytes" }, plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.ListenerReconfiguration, plan.RequiredEffect);
    }

    [Fact]
    public void Compare_TcpBindAddressChanged_DetectsChangeAsListenerReconfiguration()
    {
        var before = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Tcp = new YaguraConfigurationOptions.IngestionOptions.TcpOptions { BindAddress = "0.0.0.0" },
            },
        };
        var after = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Tcp = new YaguraConfigurationOptions.IngestionOptions.TcpOptions { BindAddress = "192.168.1.10" },
            },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(new[] { "Ingestion:Tcp:BindAddress" }, plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.ListenerReconfiguration, plan.RequiredEffect);
    }

    [Fact]
    public void Compare_TcpPortChanged_DetectsChangeAsListenerReconfiguration()
    {
        // Issue #210: SetupWizardService.ApplyValues が現に書き換える到達可能なキー
        // （ウィザード経由の回帰テストは SetupWizardServiceTests 側にも追加する）。
        var before = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Tcp = new YaguraConfigurationOptions.IngestionOptions.TcpOptions { Port = "514" },
            },
        };
        var after = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Tcp = new YaguraConfigurationOptions.IngestionOptions.TcpOptions { Port = "5140" },
            },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(new[] { "Ingestion:Tcp:Port" }, plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.ListenerReconfiguration, plan.RequiredEffect);
    }

    [Fact]
    public void Compare_RetentionDaysChanged_DetectsChangeAsImmediate()
    {
        // Issue #210: SetupWizardService.ApplyValues が現に書き換える到達可能なキー
        // （ウィザード経由の回帰テストは SetupWizardServiceTests 側にも追加する）。
        var before = new YaguraConfigurationOptions
        {
            Retention = new YaguraConfigurationOptions.RetentionOptions { Days = "30" },
        };
        var after = new YaguraConfigurationOptions
        {
            Retention = new YaguraConfigurationOptions.RetentionOptions { Days = "90" },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(new[] { "Retention:Days" }, plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.Immediate, plan.RequiredEffect);
    }

    [Fact]
    public void Compare_RetentionExecutionTimeOfDayChanged_DetectsChangeAsImmediate()
    {
        var before = new YaguraConfigurationOptions
        {
            Retention = new YaguraConfigurationOptions.RetentionOptions { ExecutionTimeOfDay = "02:15" },
        };
        var after = new YaguraConfigurationOptions
        {
            Retention = new YaguraConfigurationOptions.RetentionOptions { ExecutionTimeOfDay = "03:30" },
        };

        var plan = ConfigurationChangePlanner.Compare(before, after);

        Assert.Equal(new[] { "Retention:ExecutionTimeOfDay" }, plan.ChangedKeys);
        Assert.Equal(ConfigurationReloadEffect.Immediate, plan.RequiredEffect);
    }

    // ------------------------------------------------------------------
    // 網羅性テスト（Issue #210。PR #209 で試作され、当時は既存 5 漏れで即失敗するため
    // 見送られていたもの。本 Issue で Compare が全キーを比較するようになったため有効化する）。
    // ------------------------------------------------------------------

    [Fact]
    public void Compare_EveryRegisteredKey_ExceptProviderSwitchKeys_IsDetectedAsChanged()
    {
        foreach (var key in ConfigurationKeyMetadata.RegisteredKeys)
        {
            if (ProviderSwitchKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var before = new YaguraConfigurationOptions();
            var after = new YaguraConfigurationOptions();
            SetValueByKeyPath(after, key, "changed-value");

            var plan = ConfigurationChangePlanner.Compare(before, after);

            Assert.Equal(new[] { key }, plan.ChangedKeys);
        }
    }

    /// <summary>
    /// <paramref name="keyPath"/>（<c>:</c> 区切りの JSON キーパス）が指す
    /// <see cref="YaguraConfigurationOptions"/> の CLR プロパティ階層をリフレクションで辿り、
    /// 末端の <see langword="string"/> プロパティへ <paramref name="value"/> を設定する
    /// （中間の <c>*Options</c> ネストインスタンスは必要に応じて生成する）。キーパスの各区分は
    /// <see cref="YaguraConfigurationOptions"/> のプロパティ名と一致する設計（本メソッドが
    /// プロパティ未検出で例外を投げること自体が、キー命名とプロパティ命名の乖離を検出する）。
    /// </summary>
    private static void SetValueByKeyPath(YaguraConfigurationOptions root, string keyPath, string value)
    {
        var segments = keyPath.Split(':');
        object current = root;
        var currentType = typeof(YaguraConfigurationOptions);

        for (var i = 0; i < segments.Length; i++)
        {
            var property = currentType.GetProperty(segments[i])
                ?? throw new InvalidOperationException(
                    $"キー '{keyPath}' の区分 '{segments[i]}' に対応するプロパティが {currentType.Name} に見つかりません。" +
                    "ConfigurationKeyMetadata のキー文字列と YaguraConfigurationOptions のプロパティ名は一致させる設計です。");

            if (i == segments.Length - 1)
            {
                if (property.PropertyType != typeof(string))
                {
                    throw new NotSupportedException(
                        $"未対応のプロパティ型です: {property.DeclaringType?.Name}.{property.Name} ({property.PropertyType})。" +
                        "本テストのウォーカーを更新してください。");
                }

                property.SetValue(current, value);
                return;
            }

            var nested = property.GetValue(current);
            if (nested is null)
            {
                nested = Activator.CreateInstance(property.PropertyType)
                    ?? throw new InvalidOperationException($"{property.PropertyType} のインスタンス化に失敗しました。");
                property.SetValue(current, nested);
            }

            current = nested;
            currentType = property.PropertyType;
        }
    }
}
