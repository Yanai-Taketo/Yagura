using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <see cref="DpapiEmailPasswordProtector"/> の単体テスト（ADR-0017 決定 3。Issue #350）。
/// </summary>
/// <remarks>
/// round-trip・改ざん検出・接頭辞判別は共通実装（<see cref="DpapiSecretProtector"/>）に
/// 委譲されており、<see cref="DpapiConnectionStringProtectorTests"/> が同じ性質を検証済み。
/// <b>本クラスの主眼は共通実装の再検証ではなく「用途間の entropy 分離」</b>——SQL Server の
/// 接続文字列とメールのパスワードが同じ復号経路を共有していないことの機械検証である。
/// </remarks>
public sealed class DpapiEmailPasswordProtectorTests
{
    private const string SamplePassword = "p@ssw0rd-for-smtp";

    [Fact]
    public void ProtectThenTryUnprotect_RoundTripsPlaintext()
    {
        var encrypted = DpapiEmailPasswordProtector.Protect(SamplePassword);

        Assert.True(DpapiEmailPasswordProtector.IsProtected(encrypted));
        Assert.DoesNotContain(SamplePassword, encrypted, StringComparison.Ordinal);
        Assert.True(DpapiEmailPasswordProtector.TryUnprotect(encrypted, out var decrypted));
        Assert.Equal(SamplePassword, decrypted);
    }

    [Fact]
    public void EntropyNamespaces_AreSeparatedBetweenEmailAndConnectionString()
    {
        // 一方の暗号化表現をもう一方の設定キーへ貼り付けても復号できない（取り違えが
        // 構造的に失敗する）。この性質が壊れると、秘密値が意図しない用途へ横流しできてしまう。
        var emailCiphertext = DpapiEmailPasswordProtector.Protect(SamplePassword);
        var connectionCiphertext = DpapiConnectionStringProtector.Protect("Server=db;Database=Yagura");

        Assert.False(DpapiConnectionStringProtector.TryUnprotect(emailCiphertext, out var asConnectionString));
        Assert.Null(asConnectionString);

        Assert.False(DpapiEmailPasswordProtector.TryUnprotect(connectionCiphertext, out var asPassword));
        Assert.Null(asPassword);
    }

    [Fact]
    public void TryUnprotect_TamperedCiphertext_ReturnsFalse()
    {
        var encrypted = DpapiEmailPasswordProtector.Protect(SamplePassword);

        var bytes = Convert.FromBase64String(encrypted[DpapiSecretProtector.Prefix.Length..]);
        bytes[^1] ^= 0xFF;
        var tampered = DpapiSecretProtector.Prefix + Convert.ToBase64String(bytes);

        Assert.False(DpapiEmailPasswordProtector.TryUnprotect(tampered, out var decrypted));
        Assert.Null(decrypted);
    }

    [Fact]
    public void TryUnprotect_PlaintextWithoutPrefix_ReturnsFalse()
    {
        // 平文の手編集値は「復号対象ではない」——呼び出し側が平文として受理する分岐へ回す。
        Assert.False(DpapiEmailPasswordProtector.IsProtected(SamplePassword));
        Assert.False(DpapiEmailPasswordProtector.TryUnprotect(SamplePassword, out var decrypted));
        Assert.Null(decrypted);
    }
}
