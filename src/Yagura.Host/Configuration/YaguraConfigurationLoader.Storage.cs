using System.Globalization;
using Yagura.Storage.Spool;

namespace Yagura.Host.Configuration;

public static partial class YaguraConfigurationLoader
{
    /// <summary>
    /// データルート配下の SQLite ファイル名を解決する。パス区切り文字を含む値は
    /// データルート脱出（パストラバーサル）につながるため不正値として扱う。
    /// </summary>
    private static string ResolveSqliteFileName(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const string defaultFileName = "yagura.db";

        var raw = options.Storage?.SqliteFileName;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultFileName;
        }

        if (raw.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        {
            return raw;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Storage:SqliteFileName",
            InvalidValue: raw,
            AppliedValue: defaultFileName,
            Reason: "ファイル名として不正な文字を含むため既定値を適用"));

        return defaultFileName;
    }

    /// <summary>
    /// 永続化 provider の選択を解決する（M5-3。database.md §1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不正な provider 名の扱い</b>: <c>sqlite</c>/<c>sqlserver</c> 以外の値（大文字小文字は
    /// 区別しない）は §1「既定値で継続」により <see cref="Configuration.StorageProvider.Sqlite"/>
    /// へフォールバックし警告する。
    /// </para>
    /// <para>
    /// <b>設計判断（本 Issue の設計判断）: provider=sqlserver かつ接続文字列が未設定の場合、
    /// 起動失敗ではなく SQLite へ縮小 + 強い警告とする</b>。理由:
    /// (1) configuration.md §1 が定める「起動失敗」の対象は<b>受信の成立に不可欠なキー</b>
    /// （受信ポート等）に限定される——永続化 provider の選択はこの基準に該当しない
    /// （受信自体は継続でき、書き込み失敗時はスプールが吸収する。architecture.md §3.2）。
    /// (2) 本製品は「ログを失わない」を最優先し、縮退（既定値・安全側フォールバック）を
    /// 起動失敗より優先する設計思想を随所で採用している（同種の前例:
    /// スプール領域が開けない場合の縮退運転——<c>[spool-degraded-mode]</c> 警告。
    /// bind 失敗時の縮小継続——configuration.md §4.1）。SQL Server への昇格を意図した
    /// 環境で接続文字列の設定漏れがあっても、**サービスが全く起動せずログを一切受信できない**
    /// 事態より、**SQLite で受信を継続しながら強い警告で気づかせる**方が「ログを失わない」
    /// 原則に忠実である。
    /// (3) database.md §1.2 の契約 3 分類（一時障害・恒久障害・容量枯渇）に照らすと、
    /// 「接続文字列が無い」は SQL Server provider を構築する<b>前</b>の設定検証段階の問題であり、
    /// provider 自体の実行時障害ではない——本メソッドが SQLite へ縮小することで、
    /// 実際に構築される provider は常に接続可能な状態が保証され、後続の
    /// <see cref="SqlServerFailureClassifier"/> 等の実行時分類の対象にはならない。
    /// </para>
    /// <para>
    /// <b>警告の強さ</b>: 通常の「既定値で継続」警告と同じ経路（<see cref="ConfigurationWarning"/>）
    /// に乗せるが、Reason 文言で「SQL Server を意図していたのに SQLite で動作している」という
    /// 事故（気づかれないと本番想定の環境が組み込み DB のまま運用され続ける）を明示する。
    /// </para>
    /// <para>
    /// <b>DPAPI 暗号化表現の復号（configuration.md §2。ADR-0004 決定 5）</b>:
    /// <c>dpapi:</c> 接頭辞付きの値は <see cref="DpapiConnectionStringProtector"/> で復号して
    /// 使用する。<b>復号失敗（改ざん・別マシンへの設定コピー）は「接続文字列不備」と同じ
    /// SQLite への縮小 + 強い警告</b>とする（起動を止めない——上記 (1)(2) と同じ判断。
    /// 復号失敗は SQL Server provider を構築する前の設定検証段階の問題であり、上記 (3) の
    /// 整理にも合流する）。接頭辞のない平文は従来どおり受理し（手編集ユーザーを壊さない。
    /// 平文 → 暗号化への自動書き戻しはしない）、資格情報入りの場合のみ
    /// <see cref="SqlServerConnectionStringCredentialGuard"/> の検出で警告する。
    /// </para>
    /// </remarks>
    private static (StorageProvider Provider, string? SqlServerConnectionString) ResolveStorageProvider(
        YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var rawProvider = options.Storage?.Provider;

        if (string.IsNullOrWhiteSpace(rawProvider) || string.Equals(rawProvider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return (StorageProvider.Sqlite, null);
        }

        if (!string.Equals(rawProvider, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Storage:Provider",
                InvalidValue: rawProvider,
                AppliedValue: "sqlite",
                Reason: "既知の provider 名（sqlite / sqlserver）ではないため既定の SQLite を適用"));

            return (StorageProvider.Sqlite, null);
        }

        var connectionString = options.Storage?.SqlServer?.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Storage:SqlServer:ConnectionString",
                InvalidValue: "(未設定)",
                AppliedValue: "sqlite への縮小",
                Reason: "Storage:Provider が sqlserver ですが接続文字列が未設定のため、" +
                    "起動を継続するために組み込み SQLite へ縮小しました。SQL Server での運用を意図する場合は" +
                    "Storage:SqlServer:ConnectionString を設定してください（本縮小は受信を止めないための" +
                    "設計判断——database.md §1「ログを失わない」原則の適用）"));

            return (StorageProvider.Sqlite, null);
        }

        // --- DPAPI 暗号化表現（dpapi:<Base64>。configuration.md §2。ADR-0004 決定 5） ---
        if (DpapiConnectionStringProtector.IsProtected(connectionString))
        {
            if (DpapiConnectionStringProtector.TryUnprotect(connectionString, out var decrypted))
            {
                return (StorageProvider.SqlServer, decrypted);
            }

            // 復号失敗（改ざん・別マシンからの yagura.json コピー——DPAPI machine スコープの
            // マシン束縛による）は「接続文字列不備」（M5-3 の未設定時）と同じ縮小側継続とする。
            // 警告に元の値は載せない（暗号文とはいえ資格情報由来の値を警告経路に流さない）。
            warnings.Add(new ConfigurationWarning(
                Key: "Storage:SqlServer:ConnectionString",
                InvalidValue: "(dpapi: 暗号化表現——復号失敗。値は記録しない)",
                AppliedValue: "sqlite への縮小",
                Reason: "DPAPI 暗号化された接続文字列を復号できないため、起動を継続するために" +
                    "組み込み SQLite へ縮小しました。原因は値の改ざん・破損、または他のマシンで" +
                    "暗号化された設定ファイルのコピーです（DPAPI machine スコープの暗号化データは" +
                    "当該マシンでのみ復号可能——configuration.md §2）。SQL Server での運用を再開するには" +
                    "昇格ウィザードで接続文字列を再入力してください（本縮小は受信を止めないための" +
                    "設計判断——database.md §1「ログを失わない」原則の適用）"));

            return (StorageProvider.Sqlite, null);
        }

        // --- 平文の接続文字列（手編集経路）は受理し、自動書き換えはしない。 ---
        // --- 資格情報入りの平文のみ警告する（configuration.md §2） ---
        if (SqlServerConnectionStringCredentialGuard.ContainsPlaintextCredential(connectionString))
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Storage:SqlServer:ConnectionString",
                InvalidValue: "(平文の SQL 認証資格情報を含む——値は記録しない)",
                AppliedValue: "(平文のまま受理して継続)",
                Reason: "接続文字列に平文のパスワードが含まれています（ADR-0004 決定 5「設定ファイルに" +
                    "平文で置かない」）。動作は継続しますが、昇格ウィザードで接続文字列を再入力すると" +
                    "DPAPI 暗号化表現（dpapi:）で保存し直せます。設定ファイルの自動書き換えは行いません" +
                    "（利用者のファイルを勝手に変更しない——configuration.md §2）"));
        }

        return (StorageProvider.SqlServer, connectionString);
    }

    private static bool ResolveSpoolEnabled(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const bool defaultEnabled = true;

        var raw = options.Spool?.Enabled;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultEnabled;
        }

        if (bool.TryParse(raw, out var enabled))
        {
            return enabled;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Spool:Enabled",
            InvalidValue: raw,
            AppliedValue: defaultEnabled.ToString(CultureInfo.InvariantCulture),
            Reason: "真偽値として不正なため既定値（有効）を適用"));

        return defaultEnabled;
    }

    /// <summary>
    /// スプールディレクトリを解決する（既定はデータルート配下の <c>spool</c>。
    /// configuration.md §2「スプールと組み込み DB の置き場所はそれぞれ設定で変更できる」）。
    /// パス区切り文字自体は許容する（絶対パスの指定を妨げないため。<see cref="Path.GetFullPath"/>
    /// で解決できない値のみ不正として扱う）。
    /// </summary>
    private static string ResolveSpoolDirectory(YaguraConfigurationOptions options, string dataRoot, List<ConfigurationWarning> warnings)
    {
        var defaultDirectory = Path.Combine(dataRoot, "spool");

        var raw = options.Spool?.Directory;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultDirectory;
        }

        try
        {
            return Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            warnings.Add(new ConfigurationWarning(
                Key: "Spool:Directory",
                InvalidValue: raw,
                AppliedValue: defaultDirectory,
                Reason: "パスとして不正なため既定値（データルート配下）を適用"));

            return defaultDirectory;
        }
    }

    /// <summary>
    /// スプールのディスク使用量上限（バイト）を解決する（既定は
    /// <see cref="SpoolConstants.DefaultQuotaBytes"/>。M-12 実測確定待ちの暫定値）。
    /// </summary>
    private static long ResolveSpoolQuotaBytes(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var defaultQuotaBytes = SpoolConstants.DefaultQuotaBytes;

        var raw = options.Spool?.QuotaBytes;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultQuotaBytes;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quotaBytes) && quotaBytes > 0)
        {
            return quotaBytes;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Spool:QuotaBytes",
            InvalidValue: raw,
            AppliedValue: defaultQuotaBytes.ToString(CultureInfo.InvariantCulture),
            Reason: "正の整数（バイト数）として不正なため既定値を適用"));

        return defaultQuotaBytes;
    }

    /// <summary>
    /// 保持期間（日数）を解決する（database.md §3・DB-1。§1「既定値で継続」）。
    /// <b>未設定時の既定は 30 日</b>（DB-1 の値。
    /// 根拠: M7-2 実測でレコード単価 ≈ メッセージ長 + 約 95 B、10 msg/s × 30 日 ≈ 7.8 GB は
    /// SQL Server Express の 10 GB 上限に収まる。容量超過は保持期間とは独立の監視が受ける
    /// 設計であり（database.md §3「ディスク空き容量・DB 容量上限への接近は保持期間とは
    /// 独立に監視・警告する」）、既定を無期限にしないことがゼロ設定ファーストラン環境の
    /// ディスク枯渇を防ぐ）。
    /// </summary>
    /// <remarks>
    /// <b>不正値時のフォールバック先は「削除しない」を維持する</b>（既定 30 日と非対称——
    /// 本 Issue の設計判断）。理由: 「既定値で継続」（§1）の趣旨は本来「製品既定へ戻す」こと
    /// だが、保持期間は他の一般キー（例: ポート番号）と異なり、フォールバックの結果が
    /// 「利用者の意図に反してログが自動的に削除され始める」という不可逆な副作用を持つ。
    /// 例えば入力ミスで <c>Retention:Days=0</c> や負数を書いた利用者は、削除を望んで
    /// いない・保持期間の意味を理解せずに設定を触っただけの可能性が高く、この場合に
    /// 30 日既定へ静かにフォールバックすると「未設定時とは異なる自動削除」が不正な入力
    /// 一つで有効化されてしまう。一方「削除しない」へのフォールバックは、既定 30 日の
    /// 環境と比べてディスク消費が増える方向にしか作用せず、その増加は§3 の独立監視
    /// （容量監視）が受ける設計になっている——安全側（実害が可逆・観測可能な側）を
    /// 優先する既存の縮小方針（bind アドレス・公開範囲の不正値と同じ流儀）に合わせる。
    /// </remarks>
    private static int? ResolveRetentionDays(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const int defaultRetentionDays = 30;

        var raw = options.Retention?.Days;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultRetentionDays;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0)
        {
            return days;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Retention:Days",
            InvalidValue: raw,
            AppliedValue: "(未設定 = 削除しない)",
            Reason: "正の整数（日数）として不正なため、意図しない自動削除の開始を避け「削除しない」を適用" +
                "（既定 30 日への自動フォールバックはしない——本 Issue の設計判断。詳細は本メソッドの remarks 参照）"));

        return null;
    }

    /// <summary>
    /// 監査記録の保持期間（日数）を解決する（SEC-2。security.md §4.2）。
    /// 未設定は既定 <b>365 日</b>（SEC-2 確定値）、不正値は <c>Retention:Days</c> と同じ
    /// 「削除しない」へフォールバックし警告する（意図せぬ自動削除で証跡を失う事故を避ける安全側。
    /// 監査記録は証跡であり、不正値を既定 365 日へ読み替えて削除を始めるより、削除を止めて
    /// 警告するほうが失うものが小さい）。
    /// </summary>
    private static int? ResolveAuditRetentionDays(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        const int defaultRetentionDays = 365;

        var raw = options.Audit?.RetentionDays;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultRetentionDays;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0)
        {
            return days;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Audit:RetentionDays",
            InvalidValue: raw,
            AppliedValue: "(未設定 = 削除しない)",
            Reason: "正の整数（日数）として不正なため、意図しない自動削除の開始を避け「削除しない」を適用" +
                "（既定 365 日への自動フォールバックはしない——Retention:Days と同じ安全側の判断）"));

        return null;
    }

    /// <summary>
    /// 保持期間削除の定期実行時刻を解決する（database.md §3。§1「既定値で継続」）。
    /// </summary>
    private static TimeOnly ResolveRetentionExecutionTimeOfDay(YaguraConfigurationOptions options, List<ConfigurationWarning> warnings)
    {
        var defaultTimeOfDay = Yagura.Host.Retention.RetentionSchedulerOptions.DefaultExecutionTimeOfDay;

        var raw = options.Retention?.ExecutionTimeOfDay;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultTimeOfDay;
        }

        if (TimeOnly.TryParseExact(raw, "HH\\:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeOfDay))
        {
            return timeOfDay;
        }

        warnings.Add(new ConfigurationWarning(
            Key: "Retention:ExecutionTimeOfDay",
            InvalidValue: raw,
            AppliedValue: defaultTimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture),
            Reason: "HH:mm 形式の時刻として不正なため既定値を適用"));

        return defaultTimeOfDay;
    }
}
