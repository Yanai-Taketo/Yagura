using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Yagura.Abstractions.Administration;

namespace Yagura.Web.Tests.ArchitectureTests;

/// <summary>
/// security.md §1 L-5 覆域限界の節「閲覧リスナ側のコンポーネントから書き込み系サービスへ
/// 到達できない分離（DI 登録の分離等）を実装設計で定め、その分離自体もアーキテクチャテスト
/// （参照関係の検査）の対象とする」の実装（M6-4。Issue #54）。
/// </summary>
/// <remarks>
/// <para>
/// <b>検査対象</b>: <c>Yagura.Web</c> アセンブリ中、名前空間が <c>Yagura.Web.Components</c>
/// またはその配下（<c>Yagura.Web.Components.Pages</c> 等）に属するクラス——閲覧リスナが
/// ホストする Razor Components（<see cref="YaguraWebViewerExtensions.MapYaguraWebViewer"/>
/// が <c>MapRazorComponents&lt;YaguraWebApp&gt;()</c> でマップする描画対象一式）。
/// </para>
/// <para>
/// <b>検査方式（リフレクション。外部ライブラリなし）</b>: 各クラスについて
/// (a) <c>[Inject]</c>（<see cref="InjectAttribute"/>）付きプロパティの型、
/// (b) 全コンストラクタの引数の型、
/// の双方を集め、いずれかが <see cref="IYaguraWriteService"/> を実装する型であれば違反とする。
/// Razor の <c>@inject</c> 構文はプロパティ + <c>[Inject]</c> 属性へコンパイルされることを
/// 実装時に実機確認済み（<c>@inject ILogStore LogStore</c> →
/// <c>[Microsoft.AspNetCore.Components.Inject] public ILogStore LogStore { get; set; }</c>
/// 相当。<c>Yagura.Web.Components.Pages.LogViewer</c> をリフレクションで確認したスパイク
/// テストの結果に基づく）。コンストラクタ注入も将来使われ得るため、両経路を検査する。
/// </para>
/// <para>
/// <b>外部ライブラリを追加しなかった理由</b>: NetArchTest.Rules 等の参照関係検査ライブラリも
/// 検討したが、本検査は「特定の属性が付いた型のフィールド/プロパティ/コンストラクタ引数の型を
/// 集めてマーカーインターフェース実装を判定する」という単純なリフレクション処理で足りる
/// （conventions.md「依存を増やさない」の判断規準に従い、標準リフレクションのみで実装した）。
/// </para>
/// <para>
/// <b>M6-4 時点の検査結果</b>: <see cref="IYaguraWriteService"/> を実装する具体クラスが
/// 1 つも存在しないため（configuration.md §3〜§7 の設定ウィザード等は M8 スコープで未実装）、
/// 本テストは「違反ゼロで green」の状態にある。将来 M8 で書き込み系サービスを追加し、
/// 誤って閲覧側コンポーネントが参照した場合に初めてこの検査が red になる
/// （「その時が来たら検査が実効化する」設計——本ファイルのクラスコメント冒頭参照）。
/// </para>
/// </remarks>
public sealed class ViewerComponentReferenceIsolationTests
{
    [Fact]
    public void ViewerComponents_DoNotReferenceAnyWriteService()
    {
        var viewerComponentTypes = GetViewerComponentTypes();

        // 「閲覧リスナ側コンポーネントが実在する」ことをまず確認する(検査対象が空集合のまま
        // 恒久的に green になる空虚な真を避ける——L-5 の許可リストテストと同じ注意)。
        Assert.NotEmpty(viewerComponentTypes);

        var violations = new List<string>();

        foreach (var type in viewerComponentTypes)
        {
            foreach (var injectedType in GetInjectedPropertyTypes(type))
            {
                if (typeof(IYaguraWriteService).IsAssignableFrom(injectedType))
                {
                    violations.Add($"{type.FullName} が [Inject] プロパティ経由で書き込み系サービス {injectedType.FullName} を参照している。");
                }
            }

            foreach (var parameterType in GetConstructorParameterTypes(type))
            {
                if (typeof(IYaguraWriteService).IsAssignableFrom(parameterType))
                {
                    violations.Add($"{type.FullName} がコンストラクタ引数経由で書き込み系サービス {parameterType.FullName} を参照している。");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 検査対象: <c>Yagura.Web</c> アセンブリ中、名前空間が <c>Yagura.Web.Components</c> と
    /// 完全一致するか、その配下（<c>Yagura.Web.Components.</c> で始まる）に属する非抽象クラス。
    /// </summary>
    private static IReadOnlyList<Type> GetViewerComponentTypes()
    {
        var webAssembly = typeof(YaguraWebViewerExtensions).Assembly;

        return webAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.Namespace is not null &&
                        (t.Namespace == "Yagura.Web.Components" || t.Namespace.StartsWith("Yagura.Web.Components.", StringComparison.Ordinal)))
            .ToList();
    }

    private static IEnumerable<Type> GetInjectedPropertyTypes(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(p => p.PropertyType);

    private static IEnumerable<Type> GetConstructorParameterTypes(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);
}
