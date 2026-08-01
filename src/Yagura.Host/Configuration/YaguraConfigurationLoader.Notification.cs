using System.Globalization;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Logging;
using Yagura.Host.Observability.ActiveNotification.SourceSilence;

namespace Yagura.Host.Configuration;

public static partial class YaguraConfigurationLoader
{
    /// <summary>
    /// メール通知（ADR-0017。opt-in・既定無効）の設定を解決する。送信可能な構成が揃って
    /// いる場合のみ <see cref="ResolvedEmailNotification"/> を返し、それ以外は
    /// <see langword="null"/>（＝送らない）を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不正な構成は「機能を無効化して起動は継続」</b>（ADR-0017 決定 2。configuration.md §1 の
    /// 縮小側継続）。メール通知は受信・保存・閲覧のいずれにも不可欠でないため、構成不備で
    /// サービスの起動そのものを止めるのは釣り合わない——ただし<b>黙って無効化はしない</b>。
    /// どの縮退経路も必ず警告を 1 件積み、起動ログと管理 UI の設定警告一覧に現れる。
    /// </para>
    /// <para>
    /// <b>「無効なら以降を検証しない」</b>: <c>Enabled</c> が false のときは他のキーを一切
    /// 見ない。既定無効の機能について、使っていない利用者の設定ファイルに残った書きかけの値で
    /// 警告を出すのは雑音でしかない（ゼロ設定ファーストランの体験を汚さない——ADR-0017 決定 1）。
    /// </para>
    /// <para>
    /// <b>SMTP-AUTH の片側のみは不正</b>（決定 3）: ユーザー名だけ・パスワードだけの構成は
    /// 「認証したいのに設定が未完成」の状態であり、匿名送信へ黙って落とすと、意図しない
    /// 相手へ認証なしで送る・サーバに拒否され続けるといった形で失敗が遅れて現れる。ここで
    /// 機能ごと無効化して警告するほうが原因に近い。
    /// </para>
    /// <para>
    /// <b>DPAPI 復号失敗も同じ扱い</b>（決定 3 の fail-notify）: 別マシンからの設定コピー・
    /// 値の破損が原因。認証なし送信へのフォールバックはしない（同上）。
    /// </para>
    /// </remarks>
    private static ResolvedEmailNotification? ResolveEmailNotification(
        YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const int defaultSmtpPort = 25;

        var email = options.Notification?.Email;

        // 警告文は既定（「認証関連のセキュリティ項目」）を使わない——メール通知の有効フラグに
        // その説明は当てはまらず、利用者を認証設定側の調査へ誤誘導する。
        if (!ResolveSecurityFlag(email?.Enabled, "Notification:Email:Enabled", warnings,
                reason: "真偽値として不正なため縮小側（無効）を適用" +
                    "（configuration.md §1 の縮小側継続——opt-in 機能は不正値で有効側へ落とさない）"))
        {
            return null;
        }

        // --- 差出人・宛先（必須） ---
        var from = email?.From?.Trim();
        if (string.IsNullOrWhiteSpace(from) || !IsPlausibleEmailAddress(from))
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Notification:Email:From",
                InvalidValue: string.IsNullOrWhiteSpace(from) ? "(未設定)" : from,
                AppliedValue: "(メール通知を無効化)",
                Reason: "メール通知が有効ですが差出人アドレスが未設定または不正です。" +
                    "起動を継続するためメール通知のみを無効化しました（ADR-0017 決定 2）"));

            return null;
        }

        var to = ResolveGroupSpecs(email?.To);
        var invalidRecipient = to.FirstOrDefault(address => !IsPlausibleEmailAddress(address));
        if (to.Count == 0 || invalidRecipient is not null)
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Notification:Email:To",
                InvalidValue: invalidRecipient ?? "(未設定)",
                AppliedValue: "(メール通知を無効化)",
                Reason: invalidRecipient is not null
                    ? "宛先アドレスに不正な値が含まれるため、一部だけ送るのではなくメール通知を" +
                        "無効化しました（宛先の取りこぼしに気づけない状態を作らない）"
                    : "メール通知が有効ですが宛先が 1 件も設定されていません。" +
                        "起動を継続するためメール通知のみを無効化しました（ADR-0017 決定 2）"));

            return null;
        }

        // --- SMTP 接続先（Host は必須。Port・Security は既定あり） ---
        var smtp = email?.Smtp;
        var host = smtp?.Host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Notification:Email:Smtp:Host",
                InvalidValue: "(未設定)",
                AppliedValue: "(メール通知を無効化)",
                Reason: "メール通知が有効ですが SMTP サーバのホスト名が未設定です。" +
                    "起動を継続するためメール通知のみを無効化しました（ADR-0017 決定 2）"));

            return null;
        }

        var port = defaultSmtpPort;
        var rawPort = smtp?.Port;
        if (!string.IsNullOrWhiteSpace(rawPort))
        {
            if (int.TryParse(rawPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
                && parsedPort is >= 1 and <= 65535)
            {
                port = parsedPort;
            }
            else
            {
                // 既定の 25 番へ黙ってフォールバックしない（ADR-0017 決定 2）——指定した覚えの
                // ないポートへ送りに行く／送信が失敗し続けるという形で、設定の誤りが
                // 「届かない」としてしか現れなくなる。
                warnings.Add(new ConfigurationWarning(
                    Key: "Notification:Email:Smtp:Port",
                    InvalidValue: rawPort,
                    AppliedValue: "(メール通知を無効化)",
                    Reason: "1〜65535 の整数として不正です。既定のポートへ黙って倒さず、" +
                        "起動を継続するためメール通知のみを無効化しました（ADR-0017 決定 2）"));

                return null;
            }
        }

        var security = EmailTransportSecurity.Auto;
        var rawSecurity = smtp?.Security?.Trim();
        if (!string.IsNullOrWhiteSpace(rawSecurity))
        {
            switch (rawSecurity.ToLowerInvariant())
            {
                case "none":
                    security = EmailTransportSecurity.None;
                    break;
                case "auto":
                    security = EmailTransportSecurity.Auto;
                    break;
                case "required":
                    security = EmailTransportSecurity.Required;
                    break;
                default:
                    // どちらへも倒さない（ADR-0017 決定 2）——緩い側（auto）へ倒せば暗号化の
                    // 意図が黙って外れ、厳しい側（required）へ倒せば送信が黙って死ぬ。
                    // どちらも「設定したのに意図どおりでない」を無音にする。
                    warnings.Add(new ConfigurationWarning(
                        Key: "Notification:Email:Smtp:Security",
                        InvalidValue: rawSecurity,
                        AppliedValue: "(メール通知を無効化)",
                        Reason: "既知の値（none / auto / required）ではありません。緩い側にも" +
                            "厳しい側にも黙って倒さず、起動を継続するためメール通知のみを" +
                            "無効化しました（ADR-0017 決定 2）"));

                    return null;
            }
        }

        // --- SMTP-AUTH（任意。ただし両方揃っているか、両方無いかのいずれかであること） ---
        var username = string.IsNullOrWhiteSpace(smtp?.Username) ? null : smtp.Username.Trim();
        var rawPassword = string.IsNullOrWhiteSpace(smtp?.Password) ? null : smtp.Password;

        if ((username is null) != (rawPassword is null))
        {
            warnings.Add(new ConfigurationWarning(
                Key: username is null ? "Notification:Email:Smtp:Username" : "Notification:Email:Smtp:Password",
                InvalidValue: "(未設定——対になるキーのみ設定されています。値は記録しない)",
                AppliedValue: "(メール通知を無効化)",
                Reason: "SMTP 認証はユーザー名とパスワードの両方が必要です。片方のみが設定されて" +
                    "いるため、認証なしの送信へ落とさずメール通知を無効化しました（ADR-0017 決定 3）"));

            return null;
        }

        string? password = null;
        if (rawPassword is not null)
        {
            if (DpapiEmailPasswordProtector.IsProtected(rawPassword))
            {
                if (!DpapiEmailPasswordProtector.TryUnprotect(rawPassword, out password))
                {
                    // 警告に値は載せない（暗号文であっても資格情報由来の値を警告経路へ流さない
                    // ——Storage:SqlServer:ConnectionString の復号失敗時と同じ作法）。
                    warnings.Add(new ConfigurationWarning(
                        Key: "Notification:Email:Smtp:Password",
                        InvalidValue: "(dpapi: 暗号化表現——復号失敗。値は記録しない)",
                        AppliedValue: "(メール通知を無効化)",
                        Reason: "DPAPI 暗号化されたパスワードを復号できないため、認証なしの送信へ" +
                            "落とさずメール通知を無効化しました。原因は値の改ざん・破損、または他の" +
                            "マシンで暗号化された設定ファイルのコピーです（DPAPI machine スコープの" +
                            "暗号化データは当該マシンでのみ復号可能——configuration.md §2）。" +
                            "管理 UI でパスワードを再入力すると復旧します（ADR-0017 決定 3）"));

                    return null;
                }
            }
            else
            {
                // 平文の手編集は受理する（利用者のファイルを勝手に書き換えない——
                // configuration.md §2。Storage:SqlServer:ConnectionString と同じ判断）。
                password = rawPassword;

                warnings.Add(new ConfigurationWarning(
                    Key: "Notification:Email:Smtp:Password",
                    InvalidValue: "(平文のパスワード——値は記録しない)",
                    AppliedValue: "(平文のまま受理して継続)",
                    Reason: "パスワードが平文で保存されています（ADR-0004 決定 5「設定ファイルに" +
                        "平文で置かない」）。動作は継続しますが、管理 UI でパスワードを再入力すると" +
                        "DPAPI 暗号化表現（dpapi:）で保存し直せます。設定ファイルの自動書き換えは" +
                        "行いません（configuration.md §2）"));
            }
        }

        // 決定 3 の能動警告: 資格情報あり + Security ≠ required は、設定保存時
        // （画面のライブバナー）だけでなく**起動時・再読み込み時にも**警告する——手編集で
        // Security を auto へ戻した場合に誰も気づけない状態を作らない。機能は無効化しない
        // （推奨からの逸脱であり不正値ではない）。
        if (password is not null && security != EmailTransportSecurity.Required)
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Notification:Email:Smtp:Security",
                InvalidValue: security == EmailTransportSecurity.None ? "none" : "auto",
                AppliedValue: "(そのまま受理して継続——不正値ではなく推奨からの逸脱)",
                Reason: "パスワードが設定されていますが、暗号化（STARTTLS）が required ではありません。" +
                    "経路上で暗号化が剥がされた場合に漏れるのは通知の内容ではなく SMTP の資格情報です" +
                    "（多くの環境では AD のアカウントと同じもの）。required への変更を強く推奨します" +
                    "（ADR-0017 決定 3）"));
        }

        return new ResolvedEmailNotification(
            From: from,
            To: to,
            SmtpHost: host,
            SmtpPort: port,
            Security: security,
            Username: username,
            Password: password);
    }

    /// <summary>
    /// 送信元の途絶検知（ADR-0018。opt-in・既定無効）のウォッチリストを解決する。
    /// 監視すべきエントリが 1 件以上ある場合のみ <see cref="ResolvedSourceSilence"/> を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>縮退はエントリ単位</b>（ADR-0018 決定 1。configuration.md §1 の 3 分類に対する第 4 の
    /// 挙動）: アドレスの形式不正・閾値の範囲外は<b>当該エントリのみ</b>を無効化して警告し、
    /// リスト全体は生かす。1 エントリのタイポで他の監視まで止めるのは巻き添えが過剰である。
    /// </para>
    /// <para>
    /// <b>空配列は「正常な空リスト」</b>（警告なし）。configuration.md §1 の「空配列 = 不正値」
    /// 規定の例外——あの規定は「空 + 機能有効 = 誰も対象にならない」文脈のものであり、
    /// 本キーは空 = 意図的な無効が自然な意味論である。
    /// </para>
    /// <para>
    /// <b>上限超過はファイル順で先頭から採用し、超過分を列挙して警告する</b>。「監視されている
    /// つもりで監視されていない」検知ギャップを黙らせないため、無効化した対象アドレスを
    /// 警告に載せる。<b>先頭への追記は末尾の既存監視を押し出す</b>（新規追加が失敗するのではなく
    /// 既存の監視が止まる向き）ため、利用者向けドキュメントでは末尾追記を推奨する（申し送り D-2）。
    /// </para>
    /// </remarks>
    private static ResolvedSourceSilence? ResolveSourceSilence(
        YaguraConfigurationOptions options, List<ConfigurationWarning> warnings, ILogger logger)
    {
        var sourceSilence = options.Notification?.SourceSilence;
        var rawWatchlist = sourceSilence?.Watchlist;

        // 未設定・空配列はいずれも「機能無効」。空配列は正常な意思表示であり警告しない。
        if (rawWatchlist is null || rawWatchlist.Count == 0)
        {
            return null;
        }

        var defaultThreshold = ResolveDefaultSilenceThreshold(sourceSilence, warnings);

        var entries = new List<SourceSilenceWatchEntry>();
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var droppedByCap = new List<string>();

        for (var index = 0; index < rawWatchlist.Count; index++)
        {
            var raw = rawWatchlist[index];
            var rawAddress = raw?.Address?.Trim();

            // --- 上限（ファイル順で先頭から採用する。ADR-0018 決定 1） ---
            if (entries.Count >= SourceSilenceConstants.MaxWatchlistEntries)
            {
                droppedByCap.Add(rawAddress ?? $"[{index}]");
                continue;
            }

            // --- アドレス（必須・形式検証・正規化） ---
            if (string.IsNullOrWhiteSpace(rawAddress) || !IPAddress.TryParse(rawAddress, out var parsedAddress))
            {
                warnings.Add(new ConfigurationWarning(
                    Key: $"{SourceSilenceWatchlistKey}[{index}]:Address",
                    InvalidValue: rawAddress ?? "(未設定)",
                    AppliedValue: "(当該エントリのみ無効化)",
                    Reason: "IP アドレスとして解釈できないため、このエントリのみ監視対象から外しました" +
                        "（他のエントリの監視は継続します）"));
                continue;
            }

            // IPv4-mapped IPv6 は IPv4 へ畳む（流量制御・Top talkers と同じ既存規約）。
            // 同一装置が 2 エントリに割れ、片方だけが更新されて他方が途絶に見える事故を防ぐ。
            if (parsedAddress.IsIPv4MappedToIPv6)
            {
                parsedAddress = parsedAddress.MapToIPv4();
            }

            var normalizedAddress = parsedAddress.ToString();

            if (!seenAddresses.Add(normalizedAddress))
            {
                warnings.Add(new ConfigurationWarning(
                    Key: $"{SourceSilenceWatchlistKey}[{index}]:Address",
                    InvalidValue: rawAddress,
                    AppliedValue: "(当該エントリのみ無効化)",
                    Reason: "同じ送信元アドレスが既に登録されているため、後から現れたエントリを外しました" +
                        "（先に現れたエントリの閾値・表示名が有効です）"));
                continue;
            }

            // --- 閾値（任意。範囲外は当該エントリのみ無効化） ---
            var threshold = defaultThreshold;
            var thresholdIsDefaulted = true;
            var rawThreshold = raw?.ThresholdMinutes?.Trim();

            if (!string.IsNullOrWhiteSpace(rawThreshold))
            {
                if (!int.TryParse(rawThreshold, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                    || minutes < SourceSilenceConstants.MinThresholdMinutes
                    || minutes > SourceSilenceConstants.MaxThresholdMinutes)
                {
                    warnings.Add(new ConfigurationWarning(
                        Key: $"{SourceSilenceWatchlistKey}[{index}]:ThresholdMinutes",
                        InvalidValue: rawThreshold,
                        AppliedValue: "(当該エントリのみ無効化)",
                        Reason: $"{SourceSilenceConstants.MinThresholdMinutes}〜" +
                            $"{SourceSilenceConstants.MaxThresholdMinutes} 分の整数ではないため、" +
                            "このエントリのみ監視対象から外しました。既定の閾値へ黙って倒すと" +
                            "「指定したつもりの閾値で監視されていない」状態になるため、" +
                            "既定値へのフォールバックはしません"));

                    seenAddresses.Remove(normalizedAddress);
                    continue;
                }

                threshold = TimeSpan.FromMinutes(minutes);
                thresholdIsDefaulted = false;
            }

            entries.Add(new SourceSilenceWatchEntry(
                parsedAddress,
                string.IsNullOrWhiteSpace(raw?.Label) ? null : raw.Label.Trim(),
                threshold,
                thresholdIsDefaulted));
        }

        if (droppedByCap.Count > 0)
        {
            warnings.Add(new ConfigurationWarning(
                Key: SourceSilenceWatchlistKey,
                InvalidValue: $"{rawWatchlist.Count} 件（上限 {SourceSilenceConstants.MaxWatchlistEntries} 件）",
                AppliedValue: $"ファイル順で先頭 {SourceSilenceConstants.MaxWatchlistEntries} 件のみ有効",
                Reason: "登録上限を超えたため、超過分を監視対象から外しました。外したアドレス: " +
                    string.Join(", ", droppedByCap) +
                    "。（先頭への追記は末尾の既存監視を押し出します——新規追加が失敗するのではなく" +
                    "既存の監視が止まる向きです。追記は末尾に行ってください）"));
        }

        if (entries.Count == 0)
        {
            return null;
        }

        // 既定値で補完したエントリは情報レベルで残す（ADR-0018 決定 1——手編集の大量投入で
        // 閾値の省略が起きやすく、「登録した = すぐ気づける」という期待と 24 時間既定のズレが
        // 黙って生じるのを防ぐ）。警告ではない——省略自体は正当な使い方である。
        var defaulted = entries.Where(entry => entry.ThresholdIsDefaulted).ToList();
        if (defaulted.Count > 0)
        {
            logger.LogInformation(
                "途絶検知のウォッチリスト {TotalCount} 件のうち {DefaultedCount} 件は閾値が未指定のため既定値 " +
                "{DefaultThreshold} を適用しました（対象: {DefaultedAddresses}）。",
                entries.Count,
                defaulted.Count,
                defaultThreshold,
                string.Join(", ", defaulted.Select(entry => entry.Address.ToString())));
        }

        return new ResolvedSourceSilence(entries);
    }

    /// <summary>
    /// 閾値を省略したエントリの補完値を解決する（既定 1440 分 = 24 時間）。
    /// </summary>
    /// <remarks>
    /// エントリ個別の閾値と違い、こちらは<b>既定値へフォールバックする</b>——本キーが不正でも
    /// 「補完値が決まらない」だけであり、監視自体を止める理由にはならない（エントリ個別の
    /// 閾値は「指定したつもりの値で監視されていない」を作るため無効化に倒す。非対称は意図的）。
    /// </remarks>
    private static TimeSpan ResolveDefaultSilenceThreshold(
        YaguraConfigurationOptions.NotificationOptions.SourceSilenceOptions? options,
        List<ConfigurationWarning> warnings)
    {
        var fallback = TimeSpan.FromMinutes(SourceSilenceConstants.DefaultThresholdMinutes);
        var raw = options?.DefaultThresholdMinutes?.Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && minutes >= SourceSilenceConstants.MinThresholdMinutes
            && minutes <= SourceSilenceConstants.MaxThresholdMinutes)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        warnings.Add(new ConfigurationWarning(
            Key: SourceSilenceDefaultThresholdKey,
            InvalidValue: raw,
            AppliedValue: $"{SourceSilenceConstants.DefaultThresholdMinutes} 分",
            Reason: $"{SourceSilenceConstants.MinThresholdMinutes}〜" +
                $"{SourceSilenceConstants.MaxThresholdMinutes} 分の整数ではないため既定値を適用" +
                "（本キーは閾値を省略したエントリの補完値であり、不正でも監視自体を止める理由に" +
                "ならないため既定へフォールバックします）"));

        return fallback;
    }

    private const string SourceSilenceWatchlistKey = "Notification:SourceSilence:Watchlist";
    private const string SourceSilenceDefaultThresholdKey = "Notification:SourceSilence:DefaultThresholdMinutes";

    /// <summary>
    /// メールアドレスとして最低限の体裁を満たすかを判定する（構成の解決段の受け皿）。
    /// </summary>
    /// <remarks>
    /// <b>ここは RFC 5321/5322 の完全な検証ではない</b>。目的は「空文字・ホスト名だけ・
    /// 記号の打ち間違い」といった明らかな誤りを設定保存の時点で拾うことであり、最終的な
    /// 可否は送信時にサーバが決める（ADR-0017 が SMTP を外部境界としている以上、
    /// 構成層で厳密に判定しても偽陰性——正当なアドレスを拒む——を作るだけになる）。
    /// </remarks>
    private static bool IsPlausibleEmailAddress(string value) =>
        System.Net.Mail.MailAddress.TryCreate(value, out _);
}
