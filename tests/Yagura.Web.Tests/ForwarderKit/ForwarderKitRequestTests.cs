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

    // ---- MSI オプトイン同梱（ADR-0008 設計条件 9） ----

    [Fact]
    public void TryCreate_NoMsiBundle_IncludeMsiFalse()
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System", null, out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.False(request!.IncludeMsi);
        Assert.Null(request.MsiBundle);
    }

    [Fact]
    public void TryCreate_MsiBundleVersionMatches_Succeeds()
    {
        var bundle = CreateBundle(versionMismatch: false, acknowledged: false);

        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System", bundle, out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(request!.IncludeMsi);
        Assert.Same(bundle, request.MsiBundle);
    }

    [Fact]
    public void TryCreate_MsiBundleVersionMismatchNotAcknowledged_Fails()
    {
        var bundle = CreateBundle(versionMismatch: true, acknowledged: false);

        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System", bundle, out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(ForwarderKitValidationError.MsiVersionMismatchNotAcknowledged, error);
    }

    [Fact]
    public void TryCreate_MsiBundleVersionMismatchAcknowledged_Succeeds()
    {
        var bundle = CreateBundle(versionMismatch: true, acknowledged: true);

        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System", bundle, out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(request!.IncludeMsi);
    }

    private static ForwarderMsiBundle CreateBundle(bool versionMismatch, bool acknowledged) =>
        new(
            FilePath: @"C:\ProgramData\Yagura\forwarder\fluent-bit-4.0.14-win64.msi",
            FileName: "fluent-bit-4.0.14-win64.msi",
            ProductVersion: "4.0.14",
            Sha256: "abc123",
            OfficialHashMatch: OfficialHashMatchResult.Unverified,
            VersionMismatch: versionMismatch,
            VersionMismatchAcknowledged: acknowledged);

    // ---- 転送方式（Issue #137: Udp（既定）/ Tcp / Tls） ----

    [Fact]
    public void TryCreate_NoModeSpecified_DefaultsToUdp()
    {
        var ok = ForwarderKitRequest.TryCreate("192.0.2.10", 514, "System", out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(ForwarderKitMode.Udp, request!.Mode);
        Assert.Null(request.TlsCaCertificatePem);
    }

    [Fact]
    public void TryCreate_ModeTls_WithCaCertificate_TrimsAndKeepsPem()
    {
        var ok = ForwarderKitRequest.TryCreate(
            "192.0.2.10", 6514, "System", msiBundle: null,
            ForwarderKitMode.Tls, "  -----BEGIN CERTIFICATE-----\nAB==\n-----END CERTIFICATE-----  ",
            out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(ForwarderKitMode.Tls, request!.Mode);
        Assert.Equal("-----BEGIN CERTIFICATE-----\nAB==\n-----END CERTIFICATE-----", request.TlsCaCertificatePem);
    }

    [Fact]
    public void TryCreate_ModeTls_WithoutCaCertificate_TlsCaCertificatePemIsNull()
    {
        var ok = ForwarderKitRequest.TryCreate(
            "192.0.2.10", 6514, "System", msiBundle: null,
            ForwarderKitMode.Tls, tlsCaCertificatePem: null,
            out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Null(request!.TlsCaCertificatePem);
    }

    [Fact]
    public void TryCreate_ModeUdp_CaCertificateProvidedAnyway_IsIgnored()
    {
        // モード切替 UI で残った入力値が無関係なモードへ混入しないことの確認
        // （TryCreate の doc コメント参照）。
        var ok = ForwarderKitRequest.TryCreate(
            "192.0.2.10", 514, "System", msiBundle: null,
            ForwarderKitMode.Udp, tlsCaCertificatePem: "leftover-pem-content",
            out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Null(request!.TlsCaCertificatePem);
    }

    [Fact]
    public void TryCreate_ModeTcp_Succeeds()
    {
        var ok = ForwarderKitRequest.TryCreate(
            "192.0.2.10", 514, "System", msiBundle: null,
            ForwarderKitMode.Tcp, tlsCaCertificatePem: null,
            out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(ForwarderKitMode.Tcp, request!.Mode);
    }
}
