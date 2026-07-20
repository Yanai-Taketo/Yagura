using MailKit.Security;
using Yagura.Host.Observability.ActiveNotification.Email;

namespace Yagura.Host.Tests.Observability.ActiveNotification.Email;

/// <summary>
/// 失敗分類と利用者向け案内（ADR-0017 委任 12。Issue #385）のテスト。
/// </summary>
/// <remarks>
/// 従来、SMTP AUTH 無効化の検出は「サニタイズ後の固定文言」への部分一致で行われており
/// 構造的に不発だった（Issue #385 の欠陥 2）。検出を送信層のサニタイズ前判定へ移した結果を
/// ここで固定する。
/// </remarks>
public sealed class EmailSendFailureGuidanceTests
{
    // ------------------------------------------------------------------
    // 認証拒否の分類（サニタイズ前の生応答で判定する——Issue #385）
    // ------------------------------------------------------------------

    [Fact]
    public void ClassifyAuthenticationRejection_M365SmtpAuthDisabledResponse_IsClassifiedAsDisabledByServer()
    {
        // M365 の実応答形（535 5.7.139）。この判定は Describe による固定文言への置換より
        // 前に行われなければ成立しない（置換後の文字列に "SmtpClientAuthentication" は現れない）。
        var exception = new AuthenticationException(
            "535: 5.7.139 Authentication unsuccessful, SmtpClientAuthentication is disabled for the Tenant.");

        Assert.Equal(
            EmailSendFailureKind.AuthenticationDisabledByServer,
            MailKitEmailSender.ClassifyAuthenticationRejection(exception));
    }

    [Fact]
    public void ClassifyAuthenticationRejection_OrdinaryCredentialRejection_IsClassifiedAsRejected()
    {
        var exception = new AuthenticationException("535: 5.7.8 Error: authentication failed: Invalid user or password");

        Assert.Equal(
            EmailSendFailureKind.AuthenticationRejected,
            MailKitEmailSender.ClassifyAuthenticationRejection(exception));
    }

    [Fact]
    public void ClassifyAuthenticationRejection_ResponseContainingOnlyTheWordDisabled_IsNotOverMatched()
    {
        // 単なる "disabled" の一致は過剰に広い（Issue #385 の指摘）——無関係な文脈の disabled で
        // 「テナントで無効化」と断定しない。
        var exception = new AuthenticationException("535: authentication failed (account disabled)");

        Assert.Equal(
            EmailSendFailureKind.AuthenticationRejected,
            MailKitEmailSender.ClassifyAuthenticationRejection(exception));
    }

    // ------------------------------------------------------------------
    // 案内文（決定 3・委任 12——「打ち直させない」）
    // ------------------------------------------------------------------

    [Fact]
    public void Describe_AuthenticationDisabledByServer_TellsTheUserNotToRetypeThePassword()
    {
        var guidance = EmailSendFailureGuidance.Describe(EmailSendFailureKind.AuthenticationDisabledByServer, null);

        Assert.Contains("入力し直しても解決しません", guidance);
        Assert.Contains("リレー", guidance);
    }

    [Fact]
    public void Describe_AuthenticationNotOffered_DoesNotBlameStartTls()
    {
        // NotSupportedException の一律 StartTlsUnavailable 写像（Issue #385 の欠陥 3）を分離した
        // 分類。暗号化の問題と誤案内しない。
        var guidance = EmailSendFailureGuidance.Describe(EmailSendFailureKind.AuthenticationNotOffered, null);

        Assert.Contains("認証（SMTP AUTH）を提供していません", guidance);
        Assert.Contains("暗号化の問題ではありません", guidance);
    }

    [Fact]
    public void Describe_UnclassifiedFailure_CarriesTheDetailForDiagnosis()
    {
        var guidance = EmailSendFailureGuidance.Describe(EmailSendFailureKind.Other, "unexpected server greeting");

        Assert.Contains("unexpected server greeting", guidance);
    }
}
