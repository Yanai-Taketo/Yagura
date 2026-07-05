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
            },
            Viewer = source.Viewer is null ? null : new YaguraConfigurationOptions.ViewerOptions
            {
                HttpPort = source.Viewer.HttpPort,
                PublicAccess = source.Viewer.PublicAccess,
            },
            Admin = source.Admin is null ? null : new YaguraConfigurationOptions.AdminOptions
            {
                HttpPort = source.Admin.HttpPort,
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
        };
    }
}
