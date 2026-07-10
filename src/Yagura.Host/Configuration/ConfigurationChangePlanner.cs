namespace Yagura.Host.Configuration;

/// <summary>
/// 変更前後の設定を比較し、必要な反映アクションの最大を計算する
/// （configuration.md §3。「ウィザードが『この変更には再起動が必要です』を出すための土台」）。
/// </summary>
/// <remarks>
/// <para>
/// 比較対象は <see cref="YaguraConfigurationOptions"/>（ファイルにそのまま書かれる生の値の
/// 形）であり、<see cref="ResolvedYaguraConfiguration"/>（環境変数上書き・既定値フォールバック
/// 適用後の実効値）ではない。ウィザード・手編集が変更するのはファイルの内容そのものであり、
/// 実効値には環境変数由来の値や §1 の安全側フォールバックが混ざるため、比較対象にすると
/// 「ファイルは変えていないのに変更ありと判定される」「ファイルを変えたのに環境変数に
/// 隠れて変更なしと判定される」といった不一致が生じる。
/// </para>
/// <para>
/// 比較するキーは <see cref="ConfigurationKeyMetadata"/> に登録済みのキーと同期させる
/// （M4-3 でスプール 3 キーを追加。Issue #191 で <c>Viewer:ReverseDns:Enabled</c> の
/// 比較漏れを追加修正——PR #190 の調査で発見され、本 Issue に記録されていた既知ギャップ）。
/// 新しいキーを <see cref="YaguraConfigurationOptions"/> に追加する際は、本クラスの
/// 比較ロジックと <see cref="ConfigurationKeyMetadata"/> の両方を同じ PR で更新する。
/// </para>
/// <para>
/// <b>Issue #191 対応時点の既知の残ギャップ</b>: 本メソッドが比較する 11 キーは
/// <see cref="ConfigurationKeyMetadata.RegisteredKeys"/>（18 キー）の真部分集合であり、
/// <c>Ingestion:Udp:ReceiveBufferBytes</c>・<c>Ingestion:Tcp:BindAddress</c>・
/// <c>Ingestion:Tcp:Port</c>・<c>Retention:Days</c>・<c>Retention:ExecutionTimeOfDay</c>
/// は未比較のまま残っている（<c>Storage:Provider</c>・<c>Storage:SqlServer:ConnectionString</c>
/// は database.md §6.1 の専用切替手順が扱うため、通常の差分適用の対象外——意図的な除外）。
/// このうち <c>Ingestion:Tcp:Port</c> と <c>Retention:Days</c> は
/// <see cref="Administration.SetupWizardService.ApplyAsync"/> が現に書き換える値であり、
/// 初期セットアップウィザードでこれらを変更しても <see cref="ConfigurationChangePlan.ChangedKeys"/>
/// に現れず、UI 表示・監査記録（security.md §4.1 の 2001）の差分要約から欠落する
/// （ReverseDns より実害の到達可能性が高い）。Issue #191 のスコープ外として本 PR では
/// 修正しない（対応 PR の body に別 Issue 起票の要否を記録した）。
/// </para>
/// </remarks>
public static class ConfigurationChangePlanner
{
    /// <summary>
    /// 変更前後の設定を比較し、<see cref="ConfigurationChangePlan"/> を計算する。
    /// </summary>
    public static ConfigurationChangePlan Compare(YaguraConfigurationOptions before, YaguraConfigurationOptions after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changedKeys = new List<string>();

        CompareKey(changedKeys, "Ingestion:Udp:BindAddress", before.Ingestion?.Udp?.BindAddress, after.Ingestion?.Udp?.BindAddress);
        CompareKey(changedKeys, "Ingestion:Udp:Port", before.Ingestion?.Udp?.Port, after.Ingestion?.Udp?.Port);
        CompareKey(changedKeys, "Ingestion:Rfc3164:DefaultTimeZone", before.Ingestion?.Rfc3164?.DefaultTimeZone, after.Ingestion?.Rfc3164?.DefaultTimeZone);
        CompareKey(changedKeys, "Viewer:HttpPort", before.Viewer?.HttpPort, after.Viewer?.HttpPort);
        CompareKey(changedKeys, "Viewer:PublicAccess", before.Viewer?.PublicAccess, after.Viewer?.PublicAccess);
        CompareKey(changedKeys, "Viewer:ReverseDns:Enabled", before.Viewer?.ReverseDns?.Enabled, after.Viewer?.ReverseDns?.Enabled);
        CompareKey(changedKeys, "Admin:HttpPort", before.Admin?.HttpPort, after.Admin?.HttpPort);
        CompareKey(changedKeys, "Storage:SqliteFileName", before.Storage?.SqliteFileName, after.Storage?.SqliteFileName);
        CompareKey(changedKeys, "Spool:Enabled", before.Spool?.Enabled, after.Spool?.Enabled);
        CompareKey(changedKeys, "Spool:Directory", before.Spool?.Directory, after.Spool?.Directory);
        CompareKey(changedKeys, "Spool:QuotaBytes", before.Spool?.QuotaBytes, after.Spool?.QuotaBytes);

        var requiredEffect = ConfigurationReloadEffect.Immediate;
        foreach (var key in changedKeys)
        {
            var effect = ConfigurationKeyMetadata.GetReloadEffect(key);
            if (effect > requiredEffect)
            {
                requiredEffect = effect;
            }
        }

        return new ConfigurationChangePlan(changedKeys, requiredEffect);
    }

    /// <summary>
    /// 値なし（<see langword="null"/>）と空文字列は同一視せず、文字列としてそのまま比較する
    /// （<see cref="YaguraConfigurationOptions"/> の各プロパティは JSON の生の値をそのまま
    /// 保持する設計であるため、序数比較で十分——大文字小文字の揺れを「意味のある変更」として
    /// 扱うかは呼び出し側のキー種別次第だが、本メソッドは「ファイルに書かれていた文字列が
    /// 変わったかどうか」を機械的に見るだけに留める）。
    /// </summary>
    private static void CompareKey(List<string> changedKeys, string key, string? beforeValue, string? afterValue)
    {
        if (!string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
        {
            changedKeys.Add(key);
        }
    }
}
