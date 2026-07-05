using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <see cref="DpapiConnectionStringProtector"/> の単体テスト（configuration.md §2。
/// ADR-0004 決定 5「v0.1: DPAPI 完動」）。
/// </summary>
/// <remarks>
/// DPAPI machine スコープの暗号文はマシン依存（他マシンでは復号不能）のため、
/// round-trip の検証は同一プロセス内で完結させる（固定の暗号文を資産として持たない。
/// CI = windows-latest でそのまま実行可能）。
/// </remarks>
public sealed class DpapiConnectionStringProtectorTests
{
    private const string SamplePlaintext = "Server=db.example.test;Database=Yagura;User Id=sa;Password=secret!;TrustServerCertificate=true";

    // ------------------------------------------------------------------
    // round-trip（暗号化 → 復号で元の平文に戻る）
    // ------------------------------------------------------------------

    [Fact]
    public void ProtectThenTryUnprotect_RoundTripsPlaintext()
    {
        var encrypted = DpapiConnectionStringProtector.Protect(SamplePlaintext);

        Assert.True(DpapiConnectionStringProtector.TryUnprotect(encrypted, out var decrypted));
        Assert.Equal(SamplePlaintext, decrypted);
    }

    [Fact]
    public void Protect_ProducesPrefixedValue_WithoutLeakingPlaintext()
    {
        var encrypted = DpapiConnectionStringProtector.Protect(SamplePlaintext);

        Assert.StartsWith(DpapiConnectionStringProtector.Prefix, encrypted, StringComparison.Ordinal);
        // 暗号化表現に平文の断片（特にパスワード）が現れないこと。
        Assert.DoesNotContain("secret!", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", encrypted, StringComparison.OrdinalIgnoreCase);
        // 接頭辞以降は正しい Base64 であること（表現形式の機械検証）。
        _ = Convert.FromBase64String(encrypted[DpapiConnectionStringProtector.Prefix.Length..]);
    }

    // ------------------------------------------------------------------
    // 接頭辞による平文との機械判別
    // ------------------------------------------------------------------

    [Fact]
    public void IsProtected_DistinguishesEncryptedRepresentationFromPlaintext()
    {
        Assert.True(DpapiConnectionStringProtector.IsProtected(
            DpapiConnectionStringProtector.Protect(SamplePlaintext)));
        Assert.False(DpapiConnectionStringProtector.IsProtected(SamplePlaintext));
        Assert.False(DpapiConnectionStringProtector.IsProtected(null));
        Assert.False(DpapiConnectionStringProtector.IsProtected(""));
        // 大文字接頭辞は暗号化表現として扱わない（Ordinal 比較——表現は本クラスだけが生成する）。
        Assert.False(DpapiConnectionStringProtector.IsProtected("DPAPI:abc"));
    }

    [Fact]
    public void GuardTreatsEncryptedRepresentationAsNonPlaintext()
    {
        // 平文検出（SqlServerConnectionStringCredentialGuard）との整合: 暗号化表現は
        // 「既に暗号化済み」として平文資格情報の検出対象から除外される。
        var encrypted = DpapiConnectionStringProtector.Protect(SamplePlaintext);

        Assert.False(SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential(encrypted));
        Assert.True(SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential(SamplePlaintext));
    }

    // ------------------------------------------------------------------
    // 復号失敗（改ざん・不正な表現）→ false（例外を漏らさない）
    // ------------------------------------------------------------------

    [Fact]
    public void TryUnprotect_TamperedCiphertext_ReturnsFalse()
    {
        var encrypted = DpapiConnectionStringProtector.Protect(SamplePlaintext);

        // 暗号文のバイト列を 1 バイト反転して改ざんを模擬する（Base64 としては正しいまま）。
        var bytes = Convert.FromBase64String(encrypted[DpapiConnectionStringProtector.Prefix.Length..]);
        bytes[^1] ^= 0xFF;
        var tampered = DpapiConnectionStringProtector.Prefix + Convert.ToBase64String(bytes);

        Assert.False(DpapiConnectionStringProtector.TryUnprotect(tampered, out var decrypted));
        Assert.Null(decrypted);
    }

    [Fact]
    public void TryUnprotect_NotBase64AfterPrefix_ReturnsFalse()
    {
        Assert.False(DpapiConnectionStringProtector.TryUnprotect("dpapi:これはBase64ではない!!", out var decrypted));
        Assert.Null(decrypted);
    }

    [Fact]
    public void TryUnprotect_PlaintextWithoutPrefix_ReturnsFalse()
    {
        Assert.False(DpapiConnectionStringProtector.TryUnprotect(SamplePlaintext, out var decrypted));
        Assert.Null(decrypted);
        Assert.False(DpapiConnectionStringProtector.TryUnprotect(null, out _));
    }
}
