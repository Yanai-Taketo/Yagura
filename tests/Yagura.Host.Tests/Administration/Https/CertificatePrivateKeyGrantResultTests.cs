using Yagura.Host.Administration.Https;

namespace Yagura.Host.Tests.Administration.Https;

/// <summary>
/// <see cref="CertificatePrivateKeyGrantResult"/> の分類の単体テスト（Issue #511 の回帰）。
/// </summary>
/// <remarks>
/// Issue #511 の修正は「既に権限がある場合は監査記録を残さない」を
/// <c>if (Succeeded &amp;&amp; !WasAlreadyGranted) { 監査 } else { 警告 }</c> で表現したため、
/// **既に権限がある**という第三の結果が else 側へ落ち、正常な構成の起動のたびに
/// 「秘密鍵読み取り権限を自動付与できませんでした（理由: (null)）」が 3 リスナ分出ていた
/// （第 4 回 lab 実機検証で検出。権限自体は付いており TLS は成立していた）。
///
/// 「監査に残すか」と「警告を出すか」は独立した問いなので、独立した述語として固定する。
/// 3 つの生成経路すべてについて**両方の述語**を表明しているのは、片方だけを見ると
/// 「否定すれば他方になる」という同じ誤りを再び通してしまうため。
/// </remarks>
public sealed class CertificatePrivateKeyGrantResultTests
{
    private const string KeyFilePath = @"C:\ProgramData\Microsoft\Crypto\Keys\dummy-key";

    [Fact]
    public void Success_RecordsAuditAndDoesNotWarn()
    {
        var result = CertificatePrivateKeyGrantResult.Success(KeyFilePath);

        Assert.True(result.Succeeded);
        Assert.True(result.ShouldRecordGrantAudit);
        Assert.False(result.ShouldWarnGrantFailed);
        Assert.Equal(KeyFilePath, result.KeyFilePath);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void AlreadyGranted_NeitherRecordsAuditNorWarns()
    {
        // Issue #511 の本体: ACL を変えていないので監査は残さず、権限は足りているので
        // 警告も出さない——**どちらでもない**のが正しい振る舞い。
        var result = CertificatePrivateKeyGrantResult.AlreadyGranted(KeyFilePath);

        Assert.True(result.Succeeded);
        Assert.False(result.ShouldRecordGrantAudit);
        Assert.False(result.ShouldWarnGrantFailed);
        Assert.Equal(KeyFilePath, result.KeyFilePath);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void Failure_WarnsAndDoesNotRecordAudit()
    {
        var result = CertificatePrivateKeyGrantResult.Failure("アクセスが拒否されました。");

        Assert.False(result.Succeeded);
        Assert.False(result.ShouldRecordGrantAudit);
        Assert.True(result.ShouldWarnGrantFailed);
        Assert.Equal("アクセスが拒否されました。", result.FailureReason);
    }

    [Fact]
    public void ShouldWarnGrantFailed_IsNotTheNegationOfShouldRecordGrantAudit()
    {
        // 二つの述語が「互いの否定」ではないことを明示的に固定する。
        // これが成り立つと信じたことが Issue #511 の修正で誤警告を生んだ。
        var alreadyGranted = CertificatePrivateKeyGrantResult.AlreadyGranted(KeyFilePath);

        Assert.False(alreadyGranted.ShouldRecordGrantAudit);
        Assert.NotEqual(!alreadyGranted.ShouldRecordGrantAudit, alreadyGranted.ShouldWarnGrantFailed);
    }

    [Fact]
    public void WarningIsAlwaysAccompaniedByAReason()
    {
        // 実機で観測された「理由: (null)」を二度と出さないための表明。
        // 警告を出す結果には必ず理由が付く（= 理由の無い結果は警告しない）。
        CertificatePrivateKeyGrantResult[] results =
        [
            CertificatePrivateKeyGrantResult.Success(KeyFilePath),
            CertificatePrivateKeyGrantResult.AlreadyGranted(KeyFilePath),
            CertificatePrivateKeyGrantResult.Failure("アクセスが拒否されました。"),
        ];

        foreach (var result in results)
        {
            if (result.ShouldWarnGrantFailed)
            {
                Assert.False(string.IsNullOrEmpty(result.FailureReason));
            }
        }
    }
}
