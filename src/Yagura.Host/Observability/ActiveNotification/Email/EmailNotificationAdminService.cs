using System.Globalization;
using System.Runtime.Versioning;
using Yagura.Abstractions.Administration;
using Yagura.Abstractions.Auditing;
using Yagura.Host.Administration;
using Yagura.Host.Configuration;

namespace Yagura.Host.Observability.ActiveNotification.Email;

/// <summary>
/// <see cref="IEmailNotificationAdminService"/> の実体（ADR-0017 決定 4・8。Issue #350）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Yagura.Host.Ingestion.Tls.IngestionTlsAdminService"/> と同じ 3 層構成に従う——
/// 正規化 → fail-closed 検証 → 読み込み → 差分計算 → 差分なしなら no-op → 複製・変更 →
/// 楽観競合つき保存 → 監査。
/// </para>
/// <para>
/// <b>保存時の検証を起動時より厳しくする理由</b>: 起動時（<see cref="YaguraConfigurationLoader"/>）は
/// 不正構成を「機能を無効化して継続」に倒すが、保存時は<b>利用者が目の前にいる</b>。無言で
/// 縮退する構成を保存させるより、その場で理由を示して拒否するほうが原因に近い。
/// 逆に言えば、ここで拒否する構成はすべて「保存できても起動時に無効化される」ものであり、
/// UI と起動時の判断が食い違う（UI では保存できないのに手編集なら動く）方向の非対称は作らない。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EmailNotificationAdminService : IEmailNotificationAdminService
{
    private const string EnabledKey = "Notification:Email:Enabled";
    private const string FromKey = "Notification:Email:From";
    private const string ToKey = "Notification:Email:To";
    private const string SmtpHostKey = "Notification:Email:Smtp:Host";
    private const string SmtpPortKey = "Notification:Email:Smtp:Port";
    private const string SmtpSecurityKey = "Notification:Email:Smtp:Security";
    private const string SmtpUsernameKey = "Notification:Email:Smtp:Username";
    private const string SmtpPasswordKey = "Notification:Email:Smtp:Password";

    private const int DefaultSmtpPort = 25;
    private const string DefaultSecurity = "auto";

    private static readonly string[] KnownSecurityValues = ["none", "auto", "required"];

    private readonly string _dataRoot;
    private readonly IAuditRecorder _auditRecorder;
    private readonly IEmailSender _sender;
    private readonly EmailNotificationQueue _queue;
    private readonly Func<EmailNotificationDispatcher?> _dispatcherAccessor;
    private readonly TimeProvider _timeProvider;

    /// <remarks>
    /// 構築の口は <c>internal</c>（引数に internal 型——キュー・ディスパッチャ——が現れるため）。
    /// 型自体は公開契約 <see cref="IEmailNotificationAdminService"/> の実体として <c>public</c> を
    /// 保つ（<see cref="Yagura.Host.Ingestion.Tls.IngestionTlsAdminService"/> と同じ扱い）。
    /// </remarks>
    internal EmailNotificationAdminService(
        string dataRoot,
        IAuditRecorder auditRecorder,
        EmailNotificationQueue queue,
        Func<EmailNotificationDispatcher?> dispatcherAccessor,
        TimeProvider? timeProvider = null)
        : this(dataRoot, auditRecorder, queue, dispatcherAccessor, new MailKitEmailSender(), timeProvider)
    {
    }

    /// <summary>テストのために送信器を差し替える口（実 SMTP へ接続せずに検証する）。</summary>
    internal EmailNotificationAdminService(
        string dataRoot,
        IAuditRecorder auditRecorder,
        EmailNotificationQueue queue,
        Func<EmailNotificationDispatcher?> dispatcherAccessor,
        IEmailSender sender,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(auditRecorder);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(dispatcherAccessor);
        ArgumentNullException.ThrowIfNull(sender);

        _dataRoot = dataRoot;
        _auditRecorder = auditRecorder;
        _queue = queue;
        _dispatcherAccessor = dispatcherAccessor;
        _sender = sender;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<EmailNotificationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        return Task.FromResult(ToStatus(snapshot.Options));
    }

    public async Task<EmailNotificationConfigureResult> ConfigureAsync(
        EmailNotificationSettings settings,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = Normalize(settings, validateForEnabled: settings.Enabled);

        if (settings.ClearPassword && normalized.Password is not null)
        {
            throw new WizardValidationException(
                "「保存済みのパスワードを削除する」と新しいパスワードの入力は同時に指定できません。" +
                "パスワードを差し替える場合は削除のチェックを外し、新しい値だけを入力してください。");
        }

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var before = snapshot.Options;
        var beforeEmail = before.Notification?.Email;

        // パスワードは「空欄 = 変更しない」（UI に再表示しないため。決定 8）。削除は
        // ClearPassword の明示操作でのみ行う——空欄に削除の意味を持たせない（PR #366 レビュー対応。
        // これがないと SMTP 認証をやめる操作が決定 3 の対検証に必ず落ちる）。
        var storedPassword = beforeEmail?.Smtp?.Password;
        var passwordChanged = settings.ClearPassword
            ? !string.IsNullOrWhiteSpace(storedPassword)
            : normalized.Password is not null;
        var effectivePassword = settings.ClearPassword
            ? null
            : normalized.Password is not null
                ? DpapiEmailPasswordProtector.Protect(normalized.Password)
                : storedPassword;

        // 認証は「両方あり」か「両方なし」のいずれかでなければならない（決定 3）。
        // ユーザー名を消したのにパスワードが残る／その逆を、保存の時点で防ぐ。
        if (settings.Enabled)
        {
            var willHaveUsername = normalized.Username is not null;
            var willHavePassword = !string.IsNullOrWhiteSpace(effectivePassword);

            if (willHaveUsername != willHavePassword)
            {
                throw new WizardValidationException(
                    "SMTP 認証を使う場合はユーザー名とパスワードの両方を入力してください。" +
                    "片方だけの構成では、認証なしの送信へ黙って切り替わることを避けるため" +
                    "メール通知そのものが無効になります。認証を使わない場合はユーザー名を空にし、" +
                    "保存済みのパスワードは「保存済みのパスワードを削除する」で削除してください。");
            }
        }

        // パスワードを設定しているのに STARTTLS が必須でない構成は、警告して受理する（決定 3）。
        var plaintextCredentialWarning =
            !string.IsNullOrWhiteSpace(effectivePassword)
            && !string.Equals(normalized.Security, "required", StringComparison.Ordinal);

        var changedKeys = ComputeChangedKeys(beforeEmail, settings.Enabled, normalized, passwordChanged);

        if (changedKeys.Count == 0)
        {
            // no-op: 保存も監査もしない（同値保存の反復で監査証跡を希釈しない）。
            return new EmailNotificationConfigureResult(
                ChangedKeys: [],
                RequiredEffect: ConfigurationApplyEffect.Immediate,
                PlaintextCredentialWarning: plaintextCredentialWarning,
                Status: ToStatus(before));
        }

        var after = YaguraConfigurationOptionsCloner.Clone(before);
        after.Notification ??= new YaguraConfigurationOptions.NotificationOptions();
        after.Notification.Email ??= new YaguraConfigurationOptions.NotificationOptions.EmailOptions();
        after.Notification.Email.Smtp ??= new YaguraConfigurationOptions.NotificationOptions.EmailOptions.SmtpOptions();

        var email = after.Notification.Email;
        email.Enabled = settings.Enabled.ToString();
        email.From = normalized.From;
        email.To = normalized.To.Count == 0 ? null : [.. normalized.To];
        email.Smtp.Host = normalized.SmtpHost;
        email.Smtp.Port = normalized.SmtpPort.ToString(CultureInfo.InvariantCulture);
        email.Smtp.Security = normalized.Security;
        email.Smtp.Username = normalized.Username;
        email.Smtp.Password = effectivePassword;

        // 楽観競合（configuration.md §3）は ConfigurationConflictException をそのまま伝播する。
        YaguraConfigurationWriter.Save(_dataRoot, after, snapshot.VersionToken);

        // 監査 2021（決定 4）: 変更キーと新値を残す。**パスワードは変更の事実のみ**。
        // 宛先・接続先は「通知がどこへ向かうか」= 流出経路そのものの定義であり、
        // キー名粒度（2016）では事後に追えない。
        //
        // **CodeQL の cs/cleartext-storage-of-sensitive-information について（2026-07-19。PR #366）**:
        // 本箇所は high 深刻度で報告されるが誤検知である。SmtpPasswordKey は設定キーの「名前」
        // （"Notification:Email:Smtp:Password" という定数文字列）であり、= の右に置くのは
        // 固定の marker 文字列のみ——パスワードの値も、その DPAPI 暗号化表現も、ここへは流れない。
        // CodeQL は「識別子名に Password を含む定数」を機微データの発生源とみなすヒューリスティックで
        // 判定しており、定数名を変えれば黙るが、それはスキャナを回避するためだけの改名になるため行わない。
        // 値が載らないことは EmailNotificationAdminServiceTests
        // .ConfigureAsync_PasswordIsEncryptedAtRestAndNeverAudited が機械的に固定している
        // （本コメントではなくそのテストが不変条件の担保である）。
        await _auditRecorder.RecordAsync(
            new AuditEvent(
                OccurredAt: _timeProvider.GetUtcNow(),
                Kind: AuditEventKind.EmailNotificationConfigured,
                RemoteAddress: operatorAddress,
                RemotePort: null,
                Detail:
                    $"{EnabledKey}={settings.Enabled} " +
                    $"{FromKey}={normalized.From ?? "(未設定)"} " +
                    $"{ToKey}={(normalized.To.Count == 0 ? "(未設定)" : string.Join(";", normalized.To))} " +
                    $"{SmtpHostKey}={normalized.SmtpHost ?? "(未設定)"} " +
                    $"{SmtpPortKey}={normalized.SmtpPort} " +
                    $"{SmtpSecurityKey}={normalized.Security} " +
                    $"{SmtpUsernameKey}={normalized.Username ?? "(未設定)"} " +
                    $"{SmtpPasswordKey}={(passwordChanged ? settings.ClearPassword ? "(削除)" : "(変更あり。値は記録しない)" : "(変更なし)")} " +
                    $"changedKeys={string.Join(",", changedKeys)}",
                AuthenticationScheme: operatorScheme,
                AuthenticatedPrincipal: operatorPrincipal),
            CancellationToken.None).ConfigureAwait(false);

        // 即時反映（決定 9）。設定の再読み込み経路（ConfigurationReloadService）を待たずに、
        // 保存したこの操作の結果をそのまま送信側へ渡す——保存直後のテスト送信・実通知が
        // 古い設定で走らないようにする。DPAPI 復号も含めて Loader と同じ経路を通す。
        ApplyToDispatcher();

        return new EmailNotificationConfigureResult(
            ChangedKeys: changedKeys,
            RequiredEffect: ConfigurationApplyEffect.Immediate,
            PlaintextCredentialWarning: plaintextCredentialWarning,
            Status: ToStatus(after));
    }

    public async Task<EmailNotificationTestResult> SendTestAsync(
        EmailNotificationSettings settings,
        string? operatorAddress = null,
        string? operatorScheme = null,
        string? operatorPrincipal = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // テスト送信は「保存前の検証」が価値であるため、Enabled の値によらず必須項目を検証する
        // （無効のまま設定を詰めて試す運用を塞がない——決定 8）。
        var normalized = Normalize(settings, validateForEnabled: true);

        // パスワード欄が空欄なら保存済みの値を使う（UI に再表示しないため。決定 8）。
        // この形態は「画面上の任意ホストへ保存済み資格情報で AUTH を試行できる」操作になるため、
        // 監査に「保存済み資格情報を使用」の別を残す。削除（ClearPassword）を指定している間は
        // フォールバックしない——削除しようとしている資格情報でのテスト送信は意図と食い違う。
        var usedStoredCredential = false;
        var password = normalized.Password;

        if (password is null && normalized.Username is not null && !settings.ClearPassword)
        {
            var stored = YaguraConfigurationWriter.Read(_dataRoot).Options.Notification?.Email?.Smtp?.Password;

            if (!string.IsNullOrWhiteSpace(stored))
            {
                usedStoredCredential = true;

                if (DpapiEmailPasswordProtector.IsProtected(stored))
                {
                    if (!DpapiEmailPasswordProtector.TryUnprotect(stored, out password))
                    {
                        throw new WizardValidationException(
                            "保存済みのパスワードを復号できませんでした。設定ファイルが他のマシンから" +
                            "コピーされたか、値が破損しています。パスワードを入力し直してください。");
                    }
                }
                else
                {
                    password = stored;
                }
            }
        }

        if ((normalized.Username is null) != (password is null))
        {
            throw new WizardValidationException(
                "SMTP 認証を使う場合はユーザー名とパスワードの両方が必要です。" +
                "認証を使わない場合は両方を空にしてください。");
        }

        var configuration = new ResolvedEmailNotification(
            From: normalized.From!,
            To: normalized.To,
            SmtpHost: normalized.SmtpHost!,
            SmtpPort: normalized.SmtpPort,
            Security: ParseSecurity(normalized.Security),
            Username: normalized.Username,
            Password: password);

        // 監査 2022（決定 8）: 接続先・宛先・成否・操作者と「保存済み資格情報の使用」の別。
        // 資格情報の値は記録しない（authenticated / storedCredentialUsed はいずれも真偽値であり、
        // ユーザー名・パスワードそのものは含まない）。
        //
        // **宛先アドレスを載せることについて**: CodeQL は cs/exposure-of-sensitive-information で
        // 「個人情報を外部の場所へ書いている」と報告する。これは ADR-0017 決定 4 の意図した
        // 記録内容であり欠陥ではない——通知の宛先は「どこへ情報が出ていくか」の定義そのもので、
        // 事後に「誰が宛先を書き換えたか」を追えなければ監査の意味を成さない。ただし帰結として
        // **監査記録・イベントログを読める者は宛先アドレスを読める**（security.md §4.1 の記録内容と
        // 同じ保護水準に置かれる）。
        async Task RecordTestAuditAsync(string resultLabel) =>
            await _auditRecorder.RecordAsync(
                new AuditEvent(
                    OccurredAt: _timeProvider.GetUtcNow(),
                    Kind: AuditEventKind.EmailNotificationTestSent,
                    RemoteAddress: operatorAddress,
                    RemotePort: null,
                    Detail:
                        $"smtpHost={configuration.SmtpHost} port={configuration.SmtpPort} " +
                        $"security={normalized.Security} from={configuration.From} " +
                        $"to={string.Join(";", configuration.To)} " +
                        $"authenticated={configuration.UsesAuthentication} " +
                        $"storedCredentialUsed={usedStoredCredential} " +
                        $"result={resultLabel}",
                    AuthenticationScheme: operatorScheme,
                    AuthenticatedPrincipal: operatorPrincipal),
                CancellationToken.None).ConfigureAwait(false);

        // キュー・流量上限を経由しない直送（テストが実通知の枠を消費しない。決定 8）。
        EmailSendResult result;
        try
        {
            result = await _sender.SendAsync(
                configuration,
                "[Yagura] テスト送信",
                BuildTestBody(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 利用者の「中止」でも接続試行の事実は監査に残す（PR #366 レビュー対応）——
            // 2022 を監査対象とした理由そのもの（未保存の値で任意ホストへ接続を試せる操作は
            // 内部ネットワークの到達性探査に転用し得る）が、接続開始直後の中止で証跡ゼロに
            // なるなら成立しない。失敗の記録と同じく、中止も結果の一種として記録する。
            await RecordTestAuditAsync("cancelled").ConfigureAwait(false);
            throw;
        }

        await RecordTestAuditAsync(result.Succeeded ? "success" : $"failure:{result.FailureKind}")
            .ConfigureAwait(false);

        return new EmailNotificationTestResult(
            Succeeded: result.Succeeded,
            Guidance: result.Succeeded
                ? EmailSendFailureGuidance.Success
                : EmailSendFailureGuidance.Describe(result.FailureKind, result.FailureDetail),
            ServerResponse: result.Succeeded ? null : result.FailureDetail,
            RejectedRecipients: result.RejectedRecipients);
    }

    /// <summary>保存後に解決済み設定を送信側へ渡す（決定 9 の即時反映）。</summary>
    private void ApplyToDispatcher()
    {
        var dispatcher = _dispatcherAccessor();
        if (dispatcher is null)
        {
            return;
        }

        // 検証・DPAPI 復号・縮退の判断はすべて Loader に委ねる（保存経路が独自に解釈しない
        // ——UI 経由と起動時で解決結果が食い違う余地を作らない）。設定ファイル全体の検証が
        // 無関係キーの不正で失敗した場合は反映を見送る（Issue #370——保存は成功しており、
        // 稼働中の送信設定は変更前のまま。状態は ToStatus の ConfigurationFileError が見せる）。
        if (TryLoadResolved(out var resolved, out _))
        {
            dispatcher.UpdateConfiguration(resolved);
        }
    }

    /// <summary>
    /// 設定ファイル全体を Loader で解決する。メール通知と無関係なキーの「起動失敗」級の不正
    /// （<see cref="ConfigurationValidationException"/>）でもここでは例外にしない——保存後の
    /// 即時反映・状態取得の呼び出し元は、メール設定画面を circuit エラーで壊すのではなく
    /// 「ファイルに別の問題がある」ことを読める形で見せる（Issue #370）。
    /// </summary>
    private bool TryLoadResolved(out ResolvedEmailNotification? resolved, out string? loadError)
    {
        try
        {
            resolved = YaguraConfigurationLoader
                .Load(_dataRoot, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
                .Configuration.EmailNotification;
            loadError = null;
            return true;
        }
        catch (ConfigurationValidationException ex)
        {
            resolved = null;
            loadError = ex.Message;
            return false;
        }
    }

    /// <summary>正規化済みの入力（検証を通過した形）。</summary>
    private sealed record NormalizedSettings(
        string? From,
        IReadOnlyList<string> To,
        string? SmtpHost,
        int SmtpPort,
        string Security,
        string? Username,
        string? Password);

    /// <summary>
    /// 入力を正規化し、<paramref name="validateForEnabled"/> が真なら必須項目を検証する。
    /// </summary>
    private static NormalizedSettings Normalize(EmailNotificationSettings settings, bool validateForEnabled)
    {
        var from = Trimmed(settings.From);
        var host = Trimmed(settings.SmtpHost);
        var username = Trimmed(settings.Username);
        var password = string.IsNullOrEmpty(settings.Password) ? null : settings.Password;

        var to = (settings.To ?? [])
            .Select(Trimmed)
            .Where(address => address is not null)
            .Select(address => address!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var security = Trimmed(settings.Security)?.ToLowerInvariant() ?? DefaultSecurity;
        if (!KnownSecurityValues.Contains(security, StringComparer.Ordinal))
        {
            throw new WizardValidationException(
                "暗号化（STARTTLS）の指定は「なし（none）」「自動（auto）」「必須（required）」の" +
                "いずれかを選択してください。");
        }

        if (settings.SmtpPort is < 1 or > 65535)
        {
            throw new WizardValidationException(
                $"SMTP のポートは 1〜65535 の範囲の数値で指定してください（既定は {DefaultSmtpPort} です）。");
        }

        if (validateForEnabled)
        {
            if (from is null || !IsPlausibleEmailAddress(from))
            {
                throw new WizardValidationException(
                    "差出人アドレスを、メールアドレスとして正しい形式で入力してください。");
            }

            if (to.Count == 0)
            {
                throw new WizardValidationException("宛先アドレスを 1 件以上入力してください。");
            }

            var invalidRecipient = to.FirstOrDefault(address => !IsPlausibleEmailAddress(address));
            if (invalidRecipient is not null)
            {
                throw new WizardValidationException(
                    $"宛先アドレス「{invalidRecipient}」がメールアドレスとして正しい形式ではありません。");
            }

            if (host is null)
            {
                throw new WizardValidationException("SMTP サーバのホスト名を入力してください。");
            }
        }

        return new NormalizedSettings(from, to, host, settings.SmtpPort, security, username, password);
    }

    /// <summary>
    /// 変更差分を<b>実効値の比較</b>で求める（永続値の生文字列の表記ゆれ——<c>true</c> /
    /// <c>True</c>、ポートの前後空白等——を変更と数えない。TLS 受信版と同じ方針）。
    /// </summary>
    private static List<string> ComputeChangedKeys(
        YaguraConfigurationOptions.NotificationOptions.EmailOptions? before,
        bool enabled,
        NormalizedSettings after,
        bool passwordChanged)
    {
        var changed = new List<string>();

        // 解釈不能な生値（手編集の "yes" 等）は既定値へ写像して比較しない——写像すると UI の
        // 表示値（既定）と一致した瞬間に no-op となり、ファイル上の不正値を保存操作で上書き
        // できなくなる（「直したのに直らない」。Issue #370）。不正 → 有効値は常に変更として扱う。
        var beforeEnabledRaw = Trimmed(before?.Enabled);
        var beforeEnabled = beforeEnabledRaw is null
            ? false
            : bool.TryParse(beforeEnabledRaw, out var parsedEnabled) ? parsedEnabled : (bool?)null;
        if (beforeEnabled != enabled)
        {
            changed.Add(EnabledKey);
        }

        if (!string.Equals(Trimmed(before?.From), after.From, StringComparison.Ordinal))
        {
            changed.Add(FromKey);
        }

        // 宛先は順序も含めて比較する（同じ集合でも並べ替えは設定ファイル上の変更である）。
        var beforeTo = (before?.To ?? []).Select(Trimmed).Where(a => a is not null).Select(a => a!).ToList();
        if (!beforeTo.SequenceEqual(after.To, StringComparer.OrdinalIgnoreCase))
        {
            changed.Add(ToKey);
        }

        if (!string.Equals(Trimmed(before?.Smtp?.Host), after.SmtpHost, StringComparison.Ordinal))
        {
            changed.Add(SmtpHostKey);
        }

        // 未設定（キーなし）は既定 25 の意味だが、解釈不能な生値は「変更あり」へ倒す（同上）。
        var beforePortRaw = Trimmed(before?.Smtp?.Port);
        var beforePort = beforePortRaw is null
            ? DefaultSmtpPort
            : int.TryParse(beforePortRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;
        if (beforePort != after.SmtpPort)
        {
            changed.Add(SmtpPortKey);
        }

        var beforeSecurity = Trimmed(before?.Smtp?.Security)?.ToLowerInvariant() ?? DefaultSecurity;
        if (!string.Equals(beforeSecurity, after.Security, StringComparison.Ordinal))
        {
            changed.Add(SmtpSecurityKey);
        }

        if (!string.Equals(Trimmed(before?.Smtp?.Username), after.Username, StringComparison.Ordinal))
        {
            changed.Add(SmtpUsernameKey);
        }

        // パスワードは値を比較しない（暗号化表現は同じ平文でも毎回異なるため比較が成立しない）。
        // 「入力があったか」だけを変更の有無とする——空欄は「変更しない」の意（決定 8）。
        if (passwordChanged)
        {
            changed.Add(SmtpPasswordKey);
        }

        return changed;
    }

    private static bool IsPlausibleEmailAddress(string value) =>
        System.Net.Mail.MailAddress.TryCreate(value, out _);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ParseBool(string? raw) => bool.TryParse(raw, out var value) && value;

    private static EmailTransportSecurity ParseSecurity(string security) => security switch
    {
        "none" => EmailTransportSecurity.None,
        "required" => EmailTransportSecurity.Required,
        _ => EmailTransportSecurity.Auto,
    };

    private static string BuildTestBody() =>
        "これは Yagura のメール通知のテスト送信です。\n" +
        "このメールが届いていれば、SMTP の設定は正しく、能動通知の宛先として使用できます。\n" +
        "\n" +
        "実際の通知は、スプールの上限接近・証明書の期限接近・認証攻撃の予兆などが発生したときに送られます。\n" +
        "同じ事象は Windows イベントログ（ソース: Yagura）にも記録され、そちらが正本です。\n";

    private EmailNotificationStatus ToStatus(YaguraConfigurationOptions options)
    {
        var email = options.Notification?.Email;
        var dispatcher = _dispatcherAccessor();

        // 「有効にしたつもりで送られていない」状態（決定 2 の縮退）を画面が検出できるようにする。
        // 無関係キーの不正でファイル全体の検証が失敗した場合は縮退判定が不能のため、
        // 誤った「送られていません」バナーは出さず、ファイル側の問題として別に見せる（Issue #370）。
        var enabled = ParseBool(email?.Enabled);
        var loadSucceeded = TryLoadResolved(out var resolved, out var loadError);

        // LastFailure は 1 回だけ読んでから分類と説明を取り出す——2 回に分けて読むと、間に
        // 送信ループの成功で null 化され「分類あり・説明 null」の不整合な対になり得る（Issue #371）。
        var lastFailure = dispatcher?.LastFailure;

        var health = new EmailNotificationChannelHealth(
            LastSuccessAt: dispatcher?.LastSuccessAt,
            LastFailureKind: lastFailure?.FailureKind.ToString(),
            // 常設カードにも平易な説明と次の一手を出す（委任 12。Issue #385——従来は生の
            // 例外メッセージをそのまま表示しており、テスト送信結果だけが平易化されていた）。
            LastFailureDetail: lastFailure is null
                ? null
                : EmailSendFailureGuidance.Describe(lastFailure.FailureKind, lastFailure.FailureDetail),
            QueueDepth: _queue.Depth,
            DroppedCount: _queue.DroppedCount,
            SuppressedCount: _queue.SuppressedCount,
            LastSuppressedAt: _queue.LastSuppressedAt,
            SuppressedCountByEventId: _queue.SuppressedCountByEventId,
            DisabledByInvalidConfiguration: loadSucceeded && enabled && resolved is null,
            ConfigurationFileError: loadError);

        return new EmailNotificationStatus(
            Enabled: enabled,
            From: email?.From,
            To: email?.To ?? [],
            SmtpHost: email?.Smtp?.Host,
            SmtpPort: int.TryParse(email?.Smtp?.Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                ? port
                : DefaultSmtpPort,
            Security: Trimmed(email?.Smtp?.Security)?.ToLowerInvariant() ?? DefaultSecurity,
            Username: email?.Smtp?.Username,
            PasswordConfigured: !string.IsNullOrWhiteSpace(email?.Smtp?.Password),
            Health: health);
    }
}
