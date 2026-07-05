using Microsoft.Data.SqlClient;

namespace Yagura.Storage.ConformanceTests;

/// <summary>
/// SQL Server 適合テスト（<see cref="SqlServerLogStoreConformanceTests"/>）の接続解決。
/// 環境変数 <c>YAGURA_TEST_SQLSERVER</c>（接続文字列。テスト対象データベースを含まないサーバ接続を
/// 想定——各テストが一意なデータベース名で <c>CREATE DATABASE</c> する）が優先、無ければ既定の
/// LocalDB インスタンス（<c>(localdb)\MSSQLLocalDB</c>）へフォールバックする（Issue #47）。
/// </summary>
internal static class SqlServerTestConnection
{
    /// <summary>
    /// 接続文字列を上書きする環境変数名。
    /// </summary>
    public const string ConnectionStringEnvironmentVariable = "YAGURA_TEST_SQLSERVER";

    /// <summary>
    /// LocalDB の既定インスタンス名（<c>sqllocaldb.exe</c> がインストール時に自動作成する
    /// 既定インスタンス。Microsoft Learn "SqlLocalDB Utility" の記載どおりの慣用名。
    /// GitHub Actions windows-latest での可用性は ci.yml 側の準備ステップで確保する）。
    /// </summary>
    private const string DefaultLocalDbServerInstance = @"(localdb)\MSSQLLocalDB";

    private static readonly Lazy<bool> AvailabilityCache = new(ProbeAvailability);

    /// <summary>
    /// マスター接続文字列（<c>Initial Catalog</c> を持たない。データベースの作成・削除に使う）。
    /// </summary>
    public static string GetMasterConnectionString()
    {
        var overridden = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            var builder = new SqlConnectionStringBuilder(overridden)
            {
                InitialCatalog = string.Empty,
            };
            return builder.ConnectionString;
        }

        return new SqlConnectionStringBuilder
        {
            DataSource = DefaultLocalDbServerInstance,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 5,
        }.ConnectionString;
    }

    /// <summary>
    /// 指定したデータベース名に対する接続文字列を組み立てる。
    /// </summary>
    public static string BuildConnectionString(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(GetMasterConnectionString())
        {
            InitialCatalog = databaseName,
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// 接続確認結果をプロセス内でキャッシュする（適合テストは多数の <c>[Fact]</c> を持ち、
    /// テストごとに毎回接続を試みると発見（discovery）時の総時間が線形に伸びるため）。
    /// </summary>
    public static bool IsAvailable() => AvailabilityCache.Value;

    /// <summary>
    /// エラーメッセージ・スキップ理由に表示する接続先の説明（秘密情報を含まない）。
    /// </summary>
    public static string DescribeTarget()
    {
        var builder = new SqlConnectionStringBuilder(GetMasterConnectionString());
        return builder.DataSource;
    }

    private static bool ProbeAvailability()
    {
        try
        {
            using var connection = new SqlConnection(GetMasterConnectionString());
            connection.Open();
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // 接続文字列の形式不正等（環境変数の設定ミス）も「利用不可」として扱う。
            return false;
        }
    }
}
