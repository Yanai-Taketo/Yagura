using System.Reflection;
using Yagura.Host.Administration;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Administration;

/// <summary>
/// <see cref="YaguraConfigurationOptionsCloner"/> の単体テスト（Issue #186）。
/// </summary>
/// <remarks>
/// <para>
/// <b>背景</b>: <c>Clone</c> の Viewer セクション複製に ADR-0007 由来の
/// <see cref="YaguraConfigurationOptions.ViewerOptions.ReverseDns"/> が含まれておらず、
/// クローン経由の経路（<see cref="Administration.SetupWizardService"/>・
/// <see cref="Administration.PromotionWizardService"/> がウィザード適用時に
/// 「読み込んだ原本を複製 → 一部フィールドだけ変更 → 複製全体を保存」するパターンで使う）で
/// 手編集済みの ReverseDns 設定が保存時に silently 失われるバグがあった。
/// </para>
/// <para>
/// <b>再発防止</b>: 個別キーのテスト（下記 <c>Clone_ReverseDnsConfigured_IsCopied</c>）に加え、
/// リフレクションで <see cref="YaguraConfigurationOptions"/> のプロパティ階層を機械的に
/// 全列挙し、Clone 前後で全プロパティが一致することを検証するテスト
/// （<c>Clone_FullyPopulatedSource_CopiesEveryProperty</c>）を用意する。新しい設定キー
/// （プロパティ）が <see cref="YaguraConfigurationOptions"/> に追加されても、本クラスの
/// Clone 実装への追加を忘れると当該プロパティは自動的に <see langword="null"/> のまま
/// 複製されるため、このテストが個別キー名を知らなくても機械的に検出できる
/// （RetentionTests の KnownKeys ⇔ KeyMetadata 双方向同期テストと同種の網羅性検証。
/// クローナーはキーパス文字列ではなく CLR プロパティのコピーであるため、比較の単位も
/// リフレクションによる CLR プロパティ階層とする）。
/// </para>
/// </remarks>
public sealed class YaguraConfigurationOptionsClonerTests
{
    // ------------------------------------------------------------------
    // 個別キーのテスト（Issue #186 の直接の再現・修正確認）
    // ------------------------------------------------------------------

    [Fact]
    public void Clone_ReverseDnsConfigured_IsCopied()
    {
        var source = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions
            {
                HttpPort = "8514",
                ReverseDns = new YaguraConfigurationOptions.ViewerOptions.ReverseDnsOptions
                {
                    Enabled = "false",
                },
            },
        };

        var clone = YaguraConfigurationOptionsCloner.Clone(source);

        Assert.NotNull(clone.Viewer);
        Assert.NotNull(clone.Viewer.ReverseDns);
        Assert.Equal("false", clone.Viewer.ReverseDns.Enabled);
    }

    [Fact]
    public void Clone_ReverseDnsNull_RemainsNull()
    {
        var source = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "8514" },
        };

        var clone = YaguraConfigurationOptionsCloner.Clone(source);

        Assert.NotNull(clone.Viewer);
        Assert.Null(clone.Viewer.ReverseDns);
    }

    [Fact]
    public void Clone_MutatingClone_DoesNotAffectSource()
    {
        // Clone が参照を使い回さず、常に新しいネストインスタンスを作ることの確認
        // （複製が深いコピーであること。ReverseDns はネスト 2 段のため代表として使う）。
        var source = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions
            {
                ReverseDns = new YaguraConfigurationOptions.ViewerOptions.ReverseDnsOptions { Enabled = "true" },
            },
        };

        var clone = YaguraConfigurationOptionsCloner.Clone(source);
        clone.Viewer!.ReverseDns!.Enabled = "false";

        Assert.Equal("true", source.Viewer!.ReverseDns!.Enabled);
    }

    // ------------------------------------------------------------------
    // 網羅性テスト（再発防止）: リフレクションで全プロパティを列挙し、
    // Clone 前後の一致を機械検証する。
    // ------------------------------------------------------------------

    [Fact]
    public void Clone_FullyPopulatedSource_CopiesEveryProperty()
    {
        var populatedLeafCount = 0;
        var source = new YaguraConfigurationOptions();
        PopulateObjectGraph(source, ref populatedLeafCount);

        // ウォーカー自体が壊れて何も辿っていない事故を防ぐガード（YaguraConfigurationOptions
        // は現時点で 15 個超の string リーフプロパティを持つ）。
        Assert.True(populatedLeafCount > 10, $"想定より少ないプロパティしか列挙されていません（{populatedLeafCount} 件）。テストのウォーカーが壊れていないか確認してください。");

        var clone = YaguraConfigurationOptionsCloner.Clone(source);

        AssertDeepEqual(source, clone, path: nameof(YaguraConfigurationOptions));
    }

    [Fact]
    public void Clone_SourceWithAllSectionsNull_ReturnsAllSectionsNull()
    {
        var source = new YaguraConfigurationOptions();

        var clone = YaguraConfigurationOptionsCloner.Clone(source);

        AssertDeepEqual(source, clone, path: nameof(YaguraConfigurationOptions));
    }

    /// <summary>
    /// <paramref name="instance"/> の公開インスタンスプロパティをリフレクションで再帰的に辿り、
    /// <see cref="string"/> プロパティには一意な値を設定し、それ以外の参照型プロパティ
    /// （<c>*Options</c> のネストクラス）は新規インスタンスを生成して再帰する。
    /// </summary>
    private static void PopulateObjectGraph(object instance, ref int leafCount)
    {
        foreach (var property in GetDataProperties(instance.GetType()))
        {
            if (property.PropertyType == typeof(string))
            {
                property.SetValue(instance, $"test-value-{leafCount++}");
                continue;
            }

            // SEC-9 のグループ一覧（List<string>。ADR-0010 Phase 4）はリーフとして扱い、一意な
            // 2 要素リストを設定する（Clone が新規リストへ深いコピーすることを検証する）。
            if (property.PropertyType == typeof(List<string>))
            {
                property.SetValue(instance, new List<string> { $"group-{leafCount}-a", $"group-{leafCount}-b" });
                leafCount++;
                continue;
            }

            // オブジェクトの配列（ADR-0018 のウォッチリスト = List<WatchlistEntryOptions>）。
            // 要素を 1 つ生成して**その中身も再帰的に埋める**——要素の各フィールドまで複製されて
            // いることを検証するため。要素の参照だけを共有する浅いコピーだと、
            // 「複製後に after を編集したら before も変わる」事故になる。
            if (property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() == typeof(List<>)
                && property.PropertyType.GetGenericArguments()[0] is { IsClass: true } elementType
                && elementType != typeof(string))
            {
                var list = (System.Collections.IList)Activator.CreateInstance(property.PropertyType)!;
                var element = Activator.CreateInstance(elementType)!;
                PopulateObjectGraph(element, ref leafCount);
                list.Add(element);
                property.SetValue(instance, list);
                continue;
            }

            // YaguraConfigurationOptions のプロパティは string? / List<string>? かネストされた参照型
            // （*Options）のみである前提。将来値型（int 等）が増えた場合は、このテストが
            // 想定外の型として例外で落ちるため、ウォーカーの更新漏れが検出できる。
            if (!property.PropertyType.IsClass)
            {
                throw new NotSupportedException(
                    $"未対応のプロパティ型です: {property.DeclaringType?.Name}.{property.Name} ({property.PropertyType})。" +
                    "本テストのウォーカーを更新してください。");
            }

            var nested = Activator.CreateInstance(property.PropertyType)
                ?? throw new InvalidOperationException($"{property.PropertyType} のインスタンス化に失敗しました。");
            property.SetValue(instance, nested);
            PopulateObjectGraph(nested, ref leafCount);
        }
    }

    /// <summary>
    /// <paramref name="expected"/> と <paramref name="actual"/> のプロパティ階層を
    /// リフレクションで再帰的に比較する（null 性・string 値の一致）。
    /// </summary>
    private static void AssertDeepEqual(object? expected, object? actual, string path)
    {
        if (expected is null)
        {
            Assert.True(actual is null, $"{path}: 複製元が null なのに複製先が非 null です。");
            return;
        }

        Assert.True(actual is not null, $"{path}: 複製元が非 null なのに複製先が null です（プロパティの複製漏れ）。");

        var type = expected.GetType();
        foreach (var property in GetDataProperties(type))
        {
            var expectedValue = property.GetValue(expected);
            var actualValue = property.GetValue(actual);
            var propertyPath = $"{path}.{property.Name}";

            if (property.PropertyType == typeof(string))
            {
                Assert.True(
                    string.Equals((string?)expectedValue, (string?)actualValue, StringComparison.Ordinal),
                    $"{propertyPath}: 複製元の値 '{expectedValue}' が複製先では '{actualValue}' でした（プロパティの複製漏れの可能性）。");
                continue;
            }

            if (property.PropertyType == typeof(List<string>))
            {
                var expectedList = (List<string>?)expectedValue;
                var actualList = (List<string>?)actualValue;
                if (expectedList is null)
                {
                    Assert.True(actualList is null, $"{propertyPath}: 複製元が null なのに複製先が非 null です。");
                }
                else
                {
                    Assert.True(actualList is not null, $"{propertyPath}: 複製元が非 null なのに複製先が null です（複製漏れ）。");
                    // 深いコピー（別インスタンス）かつ内容一致であること。
                    Assert.NotSame(expectedList, actualList);
                    Assert.Equal(expectedList, actualList);
                }

                continue;
            }

            // オブジェクトの配列（ADR-0018 のウォッチリスト）。**別インスタンスであることと、
            // 各要素の中身が一致することの両方**を確認する——要素の参照だけを共有する浅いコピーは
            // 「複製後に after を編集したら before も変わる」事故になり、ウィザードの
            // 変更前比較が壊れる（このクラスが防いでいる事故そのもの）。
            if (property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() == typeof(List<>)
                && property.PropertyType.GetGenericArguments()[0] is { IsClass: true } elementType
                && elementType != typeof(string))
            {
                var expectedList = (System.Collections.IList?)expectedValue;
                var actualList = (System.Collections.IList?)actualValue;

                if (expectedList is null)
                {
                    Assert.True(actualList is null, $"{propertyPath}: 複製元が null なのに複製先が非 null です。");
                    continue;
                }

                Assert.True(actualList is not null, $"{propertyPath}: 複製元が非 null なのに複製先が null です（複製漏れ）。");
                Assert.NotSame(expectedList, actualList);
                Assert.Equal(expectedList.Count, actualList!.Count);

                for (var i = 0; i < expectedList.Count; i++)
                {
                    Assert.NotSame(expectedList[i], actualList[i]);
                    AssertDeepEqual(expectedList[i], actualList[i], $"{propertyPath}[{i}]");
                }

                continue;
            }

            AssertDeepEqual(expectedValue, actualValue, propertyPath);
        }
    }

    /// <summary>
    /// 比較・複製の対象とする公開インスタンスプロパティを返す（<c>const</c> の
    /// <c>SectionName</c> 等はそもそも <see cref="Type.GetProperties()"/> に現れないため
    /// 除外不要）。
    /// </summary>
    private static IEnumerable<PropertyInfo> GetDataProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite);
}
