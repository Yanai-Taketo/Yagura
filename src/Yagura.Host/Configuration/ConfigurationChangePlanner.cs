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
/// 比較漏れを追加修正——PR #190 の調査で発見され、本 Issue に記録されていた既知ギャップ。
/// Issue #210 で残りの 5 キー（<c>Ingestion:Udp:ReceiveBufferBytes</c>・
/// <c>Ingestion:Tcp:BindAddress</c>・<c>Ingestion:Tcp:Port</c>・<c>Retention:Days</c>・
/// <c>Retention:ExecutionTimeOfDay</c>）の比較漏れを追加修正。ADR-0010 Phase 1 の
/// 管理 UI 認証 4 キー——PR #217——は #218 とのマージ順の意味的競合で漏れたため追補。
/// ADR-0010 Phase 2 のリモートバインド + HTTPS 4 キー（<c>Admin:RemoteBinding:Enabled</c>・
/// <c>Admin:Https:Enabled</c>・<c>Admin:Https:CertificateThumbprint</c>・<c>Admin:Https:Port</c>）
/// は導入と同じ PR で本メソッドへ追加した——Phase 1 の教訓（新キー追加と比較ロジック更新を
/// 同じ PR で行う）をそのまま踏襲）。
/// 新しいキーを <see cref="YaguraConfigurationOptions"/> に追加する際は、本クラスの
/// 比較ロジックと <see cref="ConfigurationKeyMetadata"/> の両方を同じ PR で更新する。
/// </para>
/// <para>
/// <b>意図的な除外</b>: <see cref="ConfigurationKeyMetadata.RegisteredKeys"/> のうち
/// <c>Storage:Provider</c>・<c>Storage:SqlServer:ConnectionString</c> の 2 キーは本メソッドの
/// 比較対象に含めない——database.md §6.1 の専用切替手順が扱うため、通常の差分適用の対象外
/// である。それ以外の登録済みキーは全て比較する。この網羅性は
/// <c>ConfigurationChangePlannerTests</c> のリフレクションによる機械検証テストで担保する
/// （新しいキーの追加時に本メソッドへの追加を忘れると、当該テストが失敗して検出する）。
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
        CompareKey(changedKeys, "Ingestion:Udp:ReceiveBufferBytes", before.Ingestion?.Udp?.ReceiveBufferBytes, after.Ingestion?.Udp?.ReceiveBufferBytes);
        CompareKey(changedKeys, "Ingestion:Tcp:BindAddress", before.Ingestion?.Tcp?.BindAddress, after.Ingestion?.Tcp?.BindAddress);
        CompareKey(changedKeys, "Ingestion:Tcp:Port", before.Ingestion?.Tcp?.Port, after.Ingestion?.Tcp?.Port);
        CompareKey(changedKeys, "Ingestion:Tls:Enabled", before.Ingestion?.Tls?.Enabled, after.Ingestion?.Tls?.Enabled);
        CompareKey(changedKeys, "Ingestion:Tls:BindAddress", before.Ingestion?.Tls?.BindAddress, after.Ingestion?.Tls?.BindAddress);
        CompareKey(changedKeys, "Ingestion:Tls:Port", before.Ingestion?.Tls?.Port, after.Ingestion?.Tls?.Port);
        CompareKey(changedKeys, "Ingestion:Tls:CertificateThumbprint", before.Ingestion?.Tls?.CertificateThumbprint, after.Ingestion?.Tls?.CertificateThumbprint);
        CompareKey(changedKeys, "Ingestion:Rfc3164:DefaultTimeZone", before.Ingestion?.Rfc3164?.DefaultTimeZone, after.Ingestion?.Rfc3164?.DefaultTimeZone);
        CompareKey(changedKeys, "Viewer:HttpPort", before.Viewer?.HttpPort, after.Viewer?.HttpPort);
        CompareKey(changedKeys, "Viewer:PublicAccess", before.Viewer?.PublicAccess, after.Viewer?.PublicAccess);
        CompareKey(changedKeys, "Viewer:ReverseDns:Enabled", before.Viewer?.ReverseDns?.Enabled, after.Viewer?.ReverseDns?.Enabled);
        CompareKey(changedKeys, "Viewer:Authentication:Windows:Enabled", before.Viewer?.Authentication?.Windows?.Enabled, after.Viewer?.Authentication?.Windows?.Enabled);
        CompareKey(changedKeys, "Viewer:Authentication:Windows:KerberosOnly", before.Viewer?.Authentication?.Windows?.KerberosOnly, after.Viewer?.Authentication?.Windows?.KerberosOnly);
        CompareKey(changedKeys, "Admin:HttpPort", before.Admin?.HttpPort, after.Admin?.HttpPort);
        CompareKey(changedKeys, "Admin:Authentication:Windows:Enabled", before.Admin?.Authentication?.Windows?.Enabled, after.Admin?.Authentication?.Windows?.Enabled);
        CompareKey(changedKeys, "Admin:Authentication:Windows:KerberosOnly", before.Admin?.Authentication?.Windows?.KerberosOnly, after.Admin?.Authentication?.Windows?.KerberosOnly);
        CompareKey(changedKeys, "Admin:Authentication:App:Enabled", before.Admin?.Authentication?.App?.Enabled, after.Admin?.Authentication?.App?.Enabled);
        CompareKey(changedKeys, "Admin:Authentication:RequireForLoopback", before.Admin?.Authentication?.RequireForLoopback, after.Admin?.Authentication?.RequireForLoopback);
        CompareKey(changedKeys, "Admin:RemoteBinding:Enabled", before.Admin?.RemoteBinding?.Enabled, after.Admin?.RemoteBinding?.Enabled);
        CompareKey(changedKeys, "Admin:Https:Enabled", before.Admin?.Https?.Enabled, after.Admin?.Https?.Enabled);
        CompareKey(changedKeys, "Admin:Https:CertificateThumbprint", before.Admin?.Https?.CertificateThumbprint, after.Admin?.Https?.CertificateThumbprint);
        CompareKey(changedKeys, "Admin:Https:Port", before.Admin?.Https?.Port, after.Admin?.Https?.Port);
        CompareKey(changedKeys, "Storage:SqliteFileName", before.Storage?.SqliteFileName, after.Storage?.SqliteFileName);
        CompareKey(changedKeys, "Spool:Enabled", before.Spool?.Enabled, after.Spool?.Enabled);
        CompareKey(changedKeys, "Spool:Directory", before.Spool?.Directory, after.Spool?.Directory);
        CompareKey(changedKeys, "Spool:QuotaBytes", before.Spool?.QuotaBytes, after.Spool?.QuotaBytes);
        CompareKey(changedKeys, "Retention:Days", before.Retention?.Days, after.Retention?.Days);
        CompareKey(changedKeys, "Retention:ExecutionTimeOfDay", before.Retention?.ExecutionTimeOfDay, after.Retention?.ExecutionTimeOfDay);

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
