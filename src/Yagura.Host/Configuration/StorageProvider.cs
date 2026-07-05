namespace Yagura.Host.Configuration;

/// <summary>
/// 永続化 provider の選択（設定キー <c>Storage:Provider</c>。M5-3。database.md §1）。
/// </summary>
public enum StorageProvider
{
    /// <summary>組み込み SQLite（既定。database.md §4。ゼロ設定ファーストラン）。</summary>
    Sqlite,

    /// <summary>SQL Server（本番第一推奨。database.md §5）。</summary>
    SqlServer,
}
