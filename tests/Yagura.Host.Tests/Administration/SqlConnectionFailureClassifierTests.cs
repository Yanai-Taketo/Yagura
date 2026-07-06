using System.ComponentModel;
using Yagura.Abstractions.Administration;
using Yagura.Host.Administration;

namespace Yagura.Host.Tests.Administration;

/// <summary>
/// <see cref="SqlConnectionFailureClassifier"/> の単体テスト（database.md §6.1 の失敗分類。
/// PR #102）。
/// </summary>
/// <remarks>
/// <see cref="Microsoft.Data.SqlClient.SqlException"/> は公開コンストラクタを持たずテストから
/// 生成できないため、エラー番号による分類は
/// <see cref="SqlConnectionFailureClassifier.ClassifySqlErrorNumber(int)"/> を直接検証し、
/// 例外連鎖の走査は <see cref="Win32Exception"/>（TLS ハンドシェイク失敗が実際に内包する型）で
/// 検証する。番号と意味の対応は Microsoft Learn の公式ドキュメントで確認済み（2026-07-07。
/// 分類器の remarks 参照）。
/// </remarks>
public sealed class SqlConnectionFailureClassifierTests
{
    [Theory]
    [InlineData(18456, PromotionConnectionFailureKind.LoginFailed)]
    [InlineData(4060, PromotionConnectionFailureKind.DatabaseNotFound)]
    [InlineData(10061, PromotionConnectionFailureKind.ServerUnreachable)]
    [InlineData(11001, PromotionConnectionFailureKind.ServerUnreachable)]
    [InlineData(53, PromotionConnectionFailureKind.ServerUnreachable)]
    [InlineData(-2, PromotionConnectionFailureKind.ServerUnreachable)]
    [InlineData(547, PromotionConnectionFailureKind.Unclassified)]
    public void ClassifySqlErrorNumber_MapsDocumentedNumbers(int number, PromotionConnectionFailureKind expected)
    {
        Assert.Equal(expected, SqlConnectionFailureClassifier.ClassifySqlErrorNumber(number));
    }

    [Fact]
    public void Classify_UntrustedRootWin32Exception_IsCertificateNotTrusted()
    {
        // SEC_E_UNTRUSTED_ROOT (0x80090325) は SqlException.Number に現れず、内側の
        // Win32Exception の NativeErrorCode に現れる（分類器の remarks——公式ドキュメント確認済み）。
        var exception = new InvalidOperationException(
            "outer",
            new Win32Exception(unchecked((int)0x80090325)));

        Assert.Equal(
            PromotionConnectionFailureKind.CertificateNotTrusted,
            SqlConnectionFailureClassifier.Classify(exception));
    }

    [Fact]
    public void Classify_ConnectionRefusedWin32Exception_IsServerUnreachable()
    {
        var exception = new InvalidOperationException("outer", new Win32Exception(10061));

        Assert.Equal(
            PromotionConnectionFailureKind.ServerUnreachable,
            SqlConnectionFailureClassifier.Classify(exception));
    }

    [Fact]
    public void Classify_UnknownException_FallsBackToUnclassified()
    {
        // 分類不能は Unclassified に落ちる——画面は生メッセージ + 汎用案内のみを出し、修復 SQL を
        // 断定提示しない（database.md §6.1 の安全側）。
        Assert.Equal(
            PromotionConnectionFailureKind.Unclassified,
            SqlConnectionFailureClassifier.Classify(new InvalidOperationException("unknown")));
    }
}
