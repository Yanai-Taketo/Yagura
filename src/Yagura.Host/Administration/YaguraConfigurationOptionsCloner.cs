using Yagura.Host.Configuration;

namespace Yagura.Host.Administration;

/// <summary>
/// <see cref="YaguraConfigurationOptions"/> の複製（ウィザードの「読み込み → 変更 → 検証 →
/// 保存」フローで、読み込んだ原本を変更前比較（<see cref="ConfigurationChangePlanner"/>）用に
/// 保つための深いコピー。M8-4）。
/// </summary>
/// <remarks>
/// <b>新しい設定キー（プロパティ）を <see cref="YaguraConfigurationOptions"/> に追加する PR は
/// 本クラスの複製対象への追加も同じ PR に含めること</b>（ChangePlanner・KnownKeys と同じ
/// 同期規約。複製漏れは「ウィザード適用で手編集済みの無関係キーが消える」事故になる）。
/// </remarks>
internal static class YaguraConfigurationOptionsCloner
{
    public static YaguraConfigurationOptions Clone(YaguraConfigurationOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new YaguraConfigurationOptions
        {
            Ingestion = source.Ingestion is null ? null : new YaguraConfigurationOptions.IngestionOptions
            {
                Udp = source.Ingestion.Udp is null ? null : new YaguraConfigurationOptions.IngestionOptions.UdpOptions
                {
                    BindAddress = source.Ingestion.Udp.BindAddress,
                    Port = source.Ingestion.Udp.Port,
                    ReceiveBufferBytes = source.Ingestion.Udp.ReceiveBufferBytes,
                },
                Tcp = source.Ingestion.Tcp is null ? null : new YaguraConfigurationOptions.IngestionOptions.TcpOptions
                {
                    BindAddress = source.Ingestion.Tcp.BindAddress,
                    Port = source.Ingestion.Tcp.Port,
                },
                Tls = source.Ingestion.Tls is null ? null : new YaguraConfigurationOptions.IngestionOptions.TlsOptions
                {
                    Enabled = source.Ingestion.Tls.Enabled,
                    BindAddress = source.Ingestion.Tls.BindAddress,
                    Port = source.Ingestion.Tls.Port,
                    CertificateThumbprint = source.Ingestion.Tls.CertificateThumbprint,
                },
                Rfc3164 = source.Ingestion.Rfc3164 is null ? null : new YaguraConfigurationOptions.IngestionOptions.Rfc3164Options
                {
                    DefaultTimeZone = source.Ingestion.Rfc3164.DefaultTimeZone,
                },
                FlowControl = source.Ingestion.FlowControl is null ? null : new YaguraConfigurationOptions.IngestionOptions.FlowControlOptions
                {
                    Enabled = source.Ingestion.FlowControl.Enabled,
                    MessagesPerSecond = source.Ingestion.FlowControl.MessagesPerSecond,
                    BurstSize = source.Ingestion.FlowControl.BurstSize,
                },
            },
            Viewer = source.Viewer is null ? null : new YaguraConfigurationOptions.ViewerOptions
            {
                HttpPort = source.Viewer.HttpPort,
                PublicAccess = source.Viewer.PublicAccess,
                ReverseDns = source.Viewer.ReverseDns is null ? null : new YaguraConfigurationOptions.ViewerOptions.ReverseDnsOptions
                {
                    Enabled = source.Viewer.ReverseDns.Enabled,
                },
                Authentication = source.Viewer.Authentication is null ? null : new YaguraConfigurationOptions.ViewerOptions.AuthenticationOptions
                {
                    Windows = source.Viewer.Authentication.Windows is null ? null : new YaguraConfigurationOptions.ViewerOptions.AuthenticationOptions.WindowsOptions
                    {
                        Enabled = source.Viewer.Authentication.Windows.Enabled,
                        KerberosOnly = source.Viewer.Authentication.Windows.KerberosOnly,
                        ViewerGroups = source.Viewer.Authentication.Windows.ViewerGroups is null ? null : new List<string>(source.Viewer.Authentication.Windows.ViewerGroups),
                        AdminGroups = source.Viewer.Authentication.Windows.AdminGroups is null ? null : new List<string>(source.Viewer.Authentication.Windows.AdminGroups),
                    },
                },
            },
            Admin = source.Admin is null ? null : new YaguraConfigurationOptions.AdminOptions
            {
                HttpPort = source.Admin.HttpPort,
                Authentication = source.Admin.Authentication is null ? null : new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions
                {
                    RequireForLoopback = source.Admin.Authentication.RequireForLoopback,
                    Windows = source.Admin.Authentication.Windows is null ? null : new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions.WindowsOptions
                    {
                        Enabled = source.Admin.Authentication.Windows.Enabled,
                        KerberosOnly = source.Admin.Authentication.Windows.KerberosOnly,
                        AdminGroups = source.Admin.Authentication.Windows.AdminGroups is null ? null : new List<string>(source.Admin.Authentication.Windows.AdminGroups),
                    },
                    App = source.Admin.Authentication.App is null ? null : new YaguraConfigurationOptions.AdminOptions.AuthenticationOptions.AppOptions
                    {
                        Enabled = source.Admin.Authentication.App.Enabled,
                    },
                },
                RemoteBinding = source.Admin.RemoteBinding is null ? null : new YaguraConfigurationOptions.AdminOptions.RemoteBindingOptions
                {
                    Enabled = source.Admin.RemoteBinding.Enabled,
                },
                Https = source.Admin.Https is null ? null : new YaguraConfigurationOptions.AdminOptions.HttpsOptions
                {
                    Enabled = source.Admin.Https.Enabled,
                    CertificateThumbprint = source.Admin.Https.CertificateThumbprint,
                    Port = source.Admin.Https.Port,
                },
            },
            Storage = source.Storage is null ? null : new YaguraConfigurationOptions.StorageOptions
            {
                SqliteFileName = source.Storage.SqliteFileName,
                Provider = source.Storage.Provider,
                SqlServer = source.Storage.SqlServer is null ? null : new YaguraConfigurationOptions.SqlServerOptions
                {
                    ConnectionString = source.Storage.SqlServer.ConnectionString,
                },
            },
            Spool = source.Spool is null ? null : new YaguraConfigurationOptions.SpoolOptions
            {
                Enabled = source.Spool.Enabled,
                Directory = source.Spool.Directory,
                QuotaBytes = source.Spool.QuotaBytes,
            },
            Retention = source.Retention is null ? null : new YaguraConfigurationOptions.RetentionOptions
            {
                Days = source.Retention.Days,
                ExecutionTimeOfDay = source.Retention.ExecutionTimeOfDay,
            },
            Audit = source.Audit is null ? null : new YaguraConfigurationOptions.AuditOptions
            {
                RetentionDays = source.Audit.RetentionDays,
            },
        };
    }
}
