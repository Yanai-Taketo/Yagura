using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// メール通知（ADR-0017。opt-in・既定無効。Issue #350 第 1 段）の設定解決
/// （<c>Notification:Email:*</c>）の単体テスト。<see cref="IngestionTlsConfigurationTests"/> と
/// 同じ構成規約（既定・縮退・未知キー検出）を踏襲する。
/// </summary>
/// <remarks>
/// <b>本テスト群が守る不変条件</b>は 2 つ:
/// (1) 構成不備でサービスの起動を止めない（縮退の結果は必ず <c>EmailNotification == null</c>）、
/// (2) <b>黙って無効化しない</b>（どの縮退経路でも警告が 1 件以上積まれる）。
/// (2) を落とすと「設定したのに何も届かないが、どこにも理由が出ない」という最も気づきにくい
/// 失敗になるため、無効化を確認するテストは必ず警告の存在も併せて確認する。
/// </remarks>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class EmailNotificationConfigurationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-email-config-test-{Guid.NewGuid():N}");

    public EmailNotificationConfigurationTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private ConfigurationLoadResult Load(string? json = null)
    {
        if (json is not null)
        {
            File.WriteAllText(Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName), json);
        }

        return YaguraConfigurationLoader.Load(_dataRoot, new FakeLogger());
    }

    /// <summary>有効なメール設定の JSON を組み立てる（個別のキーだけを差し替えて使う）。</summary>
    private static string ValidJson(
        string enabled = "true",
        string from = "yagura@example.com",
        string to = """["ops@example.com"]""",
        string smtp = """{ "Host": "smtp.example.com" }""") =>
        $$"""
        {
          "Notification": {
            "Email": {
              "Enabled": "{{enabled}}",
              "From": "{{from}}",
              "To": {{to}},
              "Smtp": {{smtp}}
            }
          }
        }
        """;

    [Fact]
    public void Load_ConfigurationFileMissing_EmailNotificationIsNullWithoutWarnings()
    {
        var result = Load();

        // ゼロ設定ファーストラン（ADR-0017 決定 1）: 存在すら意識させない——警告も出さない。
        Assert.Null(result.Configuration.EmailNotification);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.UnknownKeys);
    }

    [Fact]
    public void Load_DisabledWithIncompleteSettings_DoesNotWarnAboutOtherKeys()
    {
        // 無効なら他のキーを一切見ない（書きかけの値で雑音を出さない）。
        var result = Load(ValidJson(enabled: "false", from: "not-an-address", to: "[]", smtp: "{ }"));

        Assert.Null(result.Configuration.EmailNotification);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.UnknownKeys);
    }

    [Fact]
    public void Load_ValidMinimalSettings_ResolvesWithDefaults()
    {
        var result = Load(ValidJson());

        var email = Assert.IsType<ResolvedEmailNotification>(result.Configuration.EmailNotification);
        Assert.Equal("yagura@example.com", email.From);
        Assert.Equal(["ops@example.com"], email.To);
        Assert.Equal("smtp.example.com", email.SmtpHost);
        Assert.Equal(25, email.SmtpPort);
        Assert.Equal(EmailTransportSecurity.Auto, email.Security);
        Assert.False(email.UsesAuthentication);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.UnknownKeys);
    }

    // 期待値を enum ではなく文字列で受けるのは、xUnit のテストメソッドが public でなければ
    // ならない一方で EmailTransportSecurity が internal であるため（テストのために型の
    // 可視性を広げない）。
    [Theory]
    [InlineData("none", nameof(EmailTransportSecurity.None))]
    [InlineData("auto", nameof(EmailTransportSecurity.Auto))]
    [InlineData("required", nameof(EmailTransportSecurity.Required))]
    [InlineData("REQUIRED", nameof(EmailTransportSecurity.Required))]
    public void Load_SecurityValues_AreParsedCaseInsensitively(string raw, string expected)
    {
        var result = Load(ValidJson(smtp: $$"""{ "Host": "smtp.example.com", "Security": "{{raw}}" }"""));

        Assert.Equal(expected, result.Configuration.EmailNotification!.Security.ToString());
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_UnknownSecurityValue_DisablesEmailWithoutFallingBackEitherWay()
    {
        var result = Load(ValidJson(smtp: """{ "Host": "smtp.example.com", "Security": "starttls" }"""));

        // 緩い側（auto）へ倒せば暗号化の意図が黙って外れ、厳しい側（required）へ倒せば送信が
        // 黙って死ぬ——ADR-0017 決定 2 はどちらの無音化も拒む。
        Assert.Null(result.Configuration.EmailNotification);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Notification:Email:Smtp:Security", warning.Key);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("smtp")]
    public void Load_InvalidPort_DisablesEmailInsteadOfFallingBackToDefault(string rawPort)
    {
        var result = Load(ValidJson(smtp: $$"""{ "Host": "smtp.example.com", "Port": "{{rawPort}}" }"""));

        // 既定の 25 番へ黙って倒すと、誤りが「なぜか届かない」としてしか現れなくなる。
        Assert.Null(result.Configuration.EmailNotification);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Notification:Email:Smtp:Port", warning.Key);
    }

    [Theory]
    // 差出人が未設定・不正
    [InlineData("""{ "Notification": { "Email": { "Enabled": "true", "To": ["a@example.com"], "Smtp": { "Host": "h" } } } }""", "Notification:Email:From")]
    [InlineData("""{ "Notification": { "Email": { "Enabled": "true", "From": "nobody", "To": ["a@example.com"], "Smtp": { "Host": "h" } } } }""", "Notification:Email:From")]
    // 宛先が 0 件・不正な要素を含む
    [InlineData("""{ "Notification": { "Email": { "Enabled": "true", "From": "y@example.com", "To": [], "Smtp": { "Host": "h" } } } }""", "Notification:Email:To")]
    [InlineData("""{ "Notification": { "Email": { "Enabled": "true", "From": "y@example.com", "To": ["a@example.com", "broken"], "Smtp": { "Host": "h" } } } }""", "Notification:Email:To")]
    // SMTP ホストが未設定
    [InlineData("""{ "Notification": { "Email": { "Enabled": "true", "From": "y@example.com", "To": ["a@example.com"] } } }""", "Notification:Email:Smtp:Host")]
    // SMTP 認証の片側のみ（決定 3——認証なし送信へ黙って落とさない）
    [InlineData("""{ "Notification": { "Email": { "Enabled": "true", "From": "y@example.com", "To": ["a@example.com"], "Smtp": { "Host": "h", "Username": "u" } } } }""", "Notification:Email:Smtp:Password")]
    [InlineData("""{ "Notification": { "Email": { "Enabled": "true", "From": "y@example.com", "To": ["a@example.com"], "Smtp": { "Host": "h", "Password": "p" } } } }""", "Notification:Email:Smtp:Username")]
    // DPAPI 復号失敗（別マシンで暗号化された設定のコピー・値の破損を模す）
    [InlineData("""{ "Notification": { "Email": { "Enabled": "true", "From": "y@example.com", "To": ["a@example.com"], "Smtp": { "Host": "h", "Username": "u", "Password": "dpapi:bm90LXJlYWxseS1lbmNyeXB0ZWQ=" } } } }""", "Notification:Email:Smtp:Password")]
    public void Load_InvalidConfiguration_DisablesEmailWithWarningOnTheOffendingKey(string json, string expectedKey)
    {
        var result = Load(json);

        Assert.Null(result.Configuration.EmailNotification);
        Assert.Contains(result.Warnings, warning => warning.Key == expectedKey);
        Assert.Empty(result.UnknownKeys);
    }

    [Fact]
    public void Load_PlaintextPassword_IsAcceptedButWarned()
    {
        // 手編集の平文は受理する（利用者のファイルを勝手に書き換えない——configuration.md §2）。
        var result = Load(ValidJson(
            smtp: """{ "Host": "smtp.example.com", "Username": "yagura", "Password": "s3cret" }"""));

        var email = Assert.IsType<ResolvedEmailNotification>(result.Configuration.EmailNotification);
        Assert.True(email.UsesAuthentication);
        Assert.Equal("s3cret", email.Password);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Notification:Email:Smtp:Password", warning.Key);
        // 警告経路に資格情報そのものを流さない。
        Assert.DoesNotContain("s3cret", warning.InvalidValue);
        Assert.DoesNotContain("s3cret", warning.Reason);
    }

    [Fact]
    public void ResolvedEmailNotification_ToString_DoesNotLeakThePassword()
    {
        // 構造化ログのテンプレートへ渡した場合の既定の展開経路を塞いでいることの確認。
        var email = new ResolvedEmailNotification(
            From: "y@example.com",
            To: ["a@example.com"],
            SmtpHost: "smtp.example.com",
            SmtpPort: 587,
            Security: EmailTransportSecurity.Required,
            Username: "yagura",
            Password: "s3cret");

        var text = email.ToString();

        Assert.DoesNotContain("s3cret", text);
        Assert.DoesNotContain("yagura", text);
        Assert.Contains("smtp.example.com", text);
    }
}
