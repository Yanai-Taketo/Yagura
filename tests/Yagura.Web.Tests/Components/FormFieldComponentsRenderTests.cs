using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// フォーム入力部品の表示確認（ui.md §3.1。PR #102 で追加・変更した部品——
/// マスク入力・必須の択一・オン/オフ入力）。
/// </summary>
public sealed class FormFieldComponentsRenderTests
{
    [Fact]
    public async Task YaguraTextField_Masked_RendersPasswordInput()
    {
        // マスク付き入力（パスワード等の秘密入力——入力値を画面に表示しない。ui.md §3.1）。
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraTextField>(new()
        {
            [nameof(YaguraTextField.Label)] = "パスワード",
            [nameof(YaguraTextField.Masked)] = true,
        });

        Assert.Contains("type=\"password\"", html);
        Assert.Contains("パスワード", html);
    }

    [Fact]
    public async Task YaguraTextField_Default_RendersTextInput()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraTextField>(new()
        {
            [nameof(YaguraTextField.Label)] = "サーバ名",
        });

        Assert.Contains("type=\"text\"", html);
        Assert.DoesNotContain("type=\"password\"", html);
    }

    [Fact]
    public async Task YaguraSelectField_AllowEmptyFalse_OmitsNoneOption()
    {
        // 必須の択一（AllowEmpty=false）は「指定なし」を出さない——初期値必須の規約は
        // doc comment に明記済み（ui.md §3.1）。
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraSelectField>(new()
        {
            [nameof(YaguraSelectField.Label)] = "認証方式",
            [nameof(YaguraSelectField.AllowEmpty)] = false,
            [nameof(YaguraSelectField.Value)] = "windows",
            [nameof(YaguraSelectField.Options)] = (IReadOnlyList<YaguraSelectOption>)
            [
                new YaguraSelectOption("windows", "Windows 統合認証"),
                new YaguraSelectOption("sql", "SQL Server 認証"),
            ],
        });

        Assert.DoesNotContain(UiText.SelectNoneOption, html);
        Assert.Contains("Windows 統合認証", html);
        Assert.Contains("SQL Server 認証", html);
    }

    [Fact]
    public async Task YaguraCheckboxField_RendersCheckboxWithLabelAndHelp()
    {
        var html = await CommonComponentRenderHarness.RenderAsync<YaguraCheckboxField>(new()
        {
            [nameof(YaguraCheckboxField.Label)] = "サーバ証明書を信頼する",
            [nameof(YaguraCheckboxField.HelpText)] = "証明書の検証を省略します。",
        });

        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("サーバ証明書を信頼する", html);
        Assert.Contains("証明書の検証を省略します。", html);
    }
}
