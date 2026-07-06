using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// <see cref="ForwarderKitRequest.TryCreate"/> の境界テスト（ADR-0008 設計条件 5——
/// 置換値検証は install.ps1 の ValidatePattern を正とする）。
/// </summary>
public sealed class ForwarderKitRequestTests
{
    [Fact]
    public void TryCreate_ValidInput_Succeeds()
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System,Application", out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("192.0.2.10", request!.Host);
        Assert.Equal(514, request.Port);
        Assert.Equal(["System", "Application"], request.Channels);
        Assert.Equal("System,Application", request.ChannelsValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_HostMissing_Fails(string? host)
    {
        var ok = ForwarderKitRequest.TryCreate(host, 514, "System", out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(ForwarderKitValidationError.HostRequired, error);
    }

    [Theory]
    [InlineData("host name")] // 空白は不許可
    [InlineData("host/name")]
    [InlineData("host@name")]
    [InlineData("<script>")]
    public void TryCreate_HostInvalidCharacters_Fails(string host)
    {
        var ok = ForwarderKitRequest.TryCreate(host, 514, "System", out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(ForwarderKitValidationError.HostInvalid, error);
    }

    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("SV01.example.com")]
    [InlineData("SV01\\SQLEXPRESS")]
    [InlineData("2001:db8::1")]
    public void TryCreate_HostValidForm_Succeeds(string host)
    {
        // install.ps1 の ValidatePattern は英数字・ピリオド・ハイフン・コロンのみを許可し、
        // バックスラッシュは許可しない——SQL インスタンス名の "\" は Yagura の宛先ホストには
        // 使わない値であるため、ここでは許可パターンに従う文字集合のみを確認する。
        if (host.Contains('\\'))
        {
            var failing = ForwarderKitRequest.TryCreate(host, 514, "System", out _, out var failError);
            Assert.False(failing);
            Assert.Equal(ForwarderKitValidationError.HostInvalid, failError);
            return;
        }

        var ok = ForwarderKitRequest.TryCreate(host, 514, "System", out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(host, request!.Host);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void TryCreate_PortOutOfRange_Fails(int port)
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", port, "System", out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(ForwarderKitValidationError.PortOutOfRange, error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(514)]
    [InlineData(65535)]
    public void TryCreate_PortBoundaryValid_Succeeds(int port)
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", port, "System", out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(port, request!.Port);
    }

    [Fact]
    public void TryCreate_ChannelsNullOrEmpty_DefaultsToSystemApplication()
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, null, out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(["System", "Application"], request!.Channels);
    }

    [Fact]
    public void TryCreate_ChannelsUnknownValue_Fails()
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System,Bogus", out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(ForwarderKitValidationError.ChannelsInvalid, error);
    }

    [Fact]
    public void TryCreate_ChannelsDuplicate_Deduplicates()
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System,System,Application", out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(["System", "Application"], request!.Channels);
    }

    [Fact]
    public void TryCreate_ChannelsOutOfOrder_NormalizesToKnownOrder()
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "Security,System", out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(["System", "Security"], request!.Channels);
    }

    [Fact]
    public void TryCreate_ChannelsEmptyElement_Fails()
    {
        // 連続カンマ("System,,Application")は空要素を生むため不正とする。
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System,,Application", out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(ForwarderKitValidationError.ChannelsInvalid, error);
    }

    [Fact]
    public void TryCreate_ChannelsCaseInsensitive_Normalizes()
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "system,SECURITY", out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(["System", "Security"], request!.Channels);
    }
}
