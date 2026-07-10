using Yagura.Web.Administration.Screens;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// 生成画面のダウンロード URL クエリ組み立て（<see cref="ForwarderKitScreen.BuildDownloadQuery"/>）を
/// 固定する。特に <c>msiVersionMismatchAcknowledged</c> の付与は回帰防止の対象——このパラメータは
/// 以前は画面の URL 組み立てに含まれておらず、版不一致時に画面の確認チェックを入れてもサーバ側
/// （<c>ForwarderKitRequest.TryCreate</c> の最終防御）で常に「未確認」扱いになり 400 になっていた
/// （PR #222 レビューで確認された既存不具合。同 PR で修正）。エンドポイント側の受理は
/// <c>ForwarderKitDownloadEndpointTests.Download_IncludeMsiTrue_VersionMismatchAcknowledged_Returns200</c>
/// が固定しており、本テストは画面 → エンドポイント間のクエリ契約（画面側の送出内容）を固定する。
/// </summary>
public sealed class ForwarderKitScreenDownloadQueryTests
{
    [Fact]
    public void BuildDownloadQuery_MsiVersionMismatchAcknowledged_IncludesAcknowledgedParameter()
    {
        var query = ForwarderKitScreen.BuildDownloadQuery(
            host: "192.0.2.10",
            port: 514,
            channels: ["System"],
            architecture: ForwarderMsiArchitecture.Win64,
            includeMsi: true,
            versionMismatch: true,
            versionMismatchAcknowledged: true);

        Assert.Contains("msiVersionMismatchAcknowledged=true", query);
        Assert.Contains("includeMsi=true", query);
    }

    [Fact]
    public void BuildDownloadQuery_MsiVersionMatch_OmitsAcknowledgedParameter()
    {
        // 版一致時は確認フローが存在しないため、パラメータ自体を送らない（余計な意味を持たせない）。
        var query = ForwarderKitScreen.BuildDownloadQuery(
            host: "192.0.2.10",
            port: 514,
            channels: ["System"],
            architecture: ForwarderMsiArchitecture.Win64,
            includeMsi: true,
            versionMismatch: false,
            versionMismatchAcknowledged: false);

        Assert.DoesNotContain("msiVersionMismatchAcknowledged", query);
    }

    [Fact]
    public void BuildDownloadQuery_NoMsi_OmitsAcknowledgedParameter()
    {
        var query = ForwarderKitScreen.BuildDownloadQuery(
            host: "192.0.2.10",
            port: 514,
            channels: ["System", "Application"],
            architecture: ForwarderMsiArchitecture.Win64,
            includeMsi: false,
            versionMismatch: true,
            versionMismatchAcknowledged: true);

        Assert.Contains("includeMsi=false", query);
        Assert.DoesNotContain("msiVersionMismatchAcknowledged", query);
    }

    [Theory]
    [InlineData(ForwarderMsiArchitecture.Win64, "architecture=x64")]
    [InlineData(ForwarderMsiArchitecture.WinArm64, "architecture=arm64")]
    public void BuildDownloadQuery_Architecture_MapsToQueryValue(ForwarderMsiArchitecture architecture, string expected)
    {
        var query = ForwarderKitScreen.BuildDownloadQuery(
            host: "192.0.2.10",
            port: 514,
            channels: ["System"],
            architecture: architecture,
            includeMsi: true,
            versionMismatch: false,
            versionMismatchAcknowledged: false);

        Assert.Contains(expected, query);
    }

    [Fact]
    public void BuildDownloadQuery_HostAndChannels_AreEscaped()
    {
        var query = ForwarderKitScreen.BuildDownloadQuery(
            host: "log-server.example.jp",
            port: 5514,
            channels: ["System", "Application", "Security"],
            architecture: ForwarderMsiArchitecture.Win64,
            includeMsi: false,
            versionMismatch: false,
            versionMismatchAcknowledged: false);

        Assert.Contains("host=log-server.example.jp", query);
        Assert.Contains("port=5514", query);
        // カンマは Uri.EscapeDataString で %2C にエスケープされる。
        Assert.Contains("channels=System%2CApplication%2CSecurity", query);
    }
}
