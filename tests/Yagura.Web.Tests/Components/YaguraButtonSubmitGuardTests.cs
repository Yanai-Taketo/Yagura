using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Yagura.Web.Components.Common;

namespace Yagura.Web.Tests.Components;

/// <summary>
/// YaguraButton の二重送信ガードと例外の受け皿（Issue #372）の挙動検証。
/// </summary>
/// <remarks>
/// Blazor Server はイベントを直列化するが await 中は次のイベントが割り込めるため、
/// 保存の await 中の再クリックが古い VersionToken のまま 2 回目の Save に到達し
/// 楽観競合 → circuit エラーになっていた（PR #366 レビューで検出）。本テストは
/// (1) 実行中の再クリックがハンドラを再実行しないこと、(2) 実行中はボタンが
/// 無効表示になること、(3) ハンドラから漏れた例外が通知（state-error）へ受けられ
/// レンダラの未処理例外にならないことを固定する。
/// </remarks>
public sealed class YaguraButtonSubmitGuardTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly RecordingNotifier _notifier = new();

    public YaguraButtonSubmitGuardTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<IYaguraNotifier>(_notifier);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ClickWhileHandlerAwaits_DoesNotInvokeHandlerAgain_AndDisablesButton()
    {
        var invocationCount = 0;
        var gate = new TaskCompletionSource();

        var cut = _ctx.Render<YaguraButton>(parameters => parameters
            .Add(b => b.ChildContent, "適用する")
            .Add(b => b.OnClick, EventCallback.Factory.Create<MouseEventArgs>(this, async () =>
            {
                invocationCount++;
                await gate.Task;
            })));

        // 1 回目: ハンドラが await で停止している間、ボタンは無効表示になる。
        var firstClick = cut.Find("button").ClickAsync(new MouseEventArgs());
        Assert.Equal(1, invocationCount);
        Assert.True(cut.Find("button").HasAttribute("disabled"));

        // 2 回目: 無効表示の再描画が届く前に発火したクリック相当（bUnit は disabled でも
        // イベントを配送できるため、再入検査そのものを検証できる）。ハンドラは再実行されない。
        await cut.Find("button").ClickAsync(new MouseEventArgs());
        Assert.Equal(1, invocationCount);

        // 完了後: ガードが解除され、ボタンは再び有効になる。
        gate.SetResult();
        await firstClick;
        Assert.False(cut.Find("button").HasAttribute("disabled"));

        // 次のクリックは通常どおり実行される（ガードの掛けっぱなし防止）。
        gate.TrySetResult();
        await cut.Find("button").ClickAsync(new MouseEventArgs());
        Assert.Equal(2, invocationCount);
    }

    [Fact]
    public async Task HandlerThrows_RoutesToErrorNotification_InsteadOfUnhandledException()
    {
        var cut = _ctx.Render<YaguraButton>(parameters => parameters
            .Add(b => b.ChildContent, "適用する")
            .Add(b => b.OnClick, EventCallback.Factory.Create<MouseEventArgs>(this,
                () => throw new InvalidOperationException("競合しました"))));

        // 例外がレンダラへ伝播すれば ClickAsync が throw する——しないことが受け皿の検証。
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        var message = Assert.Single(_notifier.Errors);
        Assert.Contains("競合しました", message);

        // 受け皿経由でもガードは解除されている（以後の操作を塞がない）。
        Assert.False(cut.Find("button").HasAttribute("disabled"));
    }

    /// <summary>通知呼び出しを記録するフェイク（表示は検証対象外）。</summary>
    private sealed class RecordingNotifier : IYaguraNotifier
    {
        public List<string> Errors { get; } = [];

        public void NotifyOk(string message)
        {
        }

        public void NotifyInfo(string message)
        {
        }

        public void NotifyWarning(string message)
        {
        }

        public void NotifyError(string message) => Errors.Add(message);
    }
}
