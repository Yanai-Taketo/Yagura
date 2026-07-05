using Microsoft.Data.SqlClient;
using Xunit;
using Yagura.Storage;
using Yagura.Storage.SqlServer;

namespace Yagura.Storage.ConformanceTests;

/// <summary>
/// <see cref="SqlServerLogStore"/> に対する適合テストスイートの実行（database.md §1.3。Issue #47）。
/// </summary>
/// <remarks>
/// <para>
/// <b>接続解決</b>: 環境変数 <see cref="SqlServerTestConnection.ConnectionStringEnvironmentVariable"/>
/// （<c>YAGURA_TEST_SQLSERVER</c>）があればそれを、無ければ既定の LocalDB インスタンス
/// （<c>(localdb)\MSSQLLocalDB</c>）を使う。テスト DB は <see cref="CreateStoreAsync"/> 呼び出しごとに
/// 一意な名前で作成し、<see cref="DisposeStoreAsync"/> で <c>DROP DATABASE</c> する
/// （テスト間でデータを共有しない——<see cref="LogStoreConformanceTestBase"/> の要求）。
/// </para>
/// <para>
/// <b>スキップ戦略</b>: 接続確認（<see cref="SqlServerTestConnection.IsAvailable"/>）に失敗した場合、
/// <see cref="Xunit.Skip.If(bool, string)"/> により <see cref="SkipException"/> を送出する。
/// <see cref="LogStoreConformanceTestBase"/> の全テストが <c>[SkippableFact]</c>/<c>[SkippableTheory]</c>
/// であるため（本クラスの派生元 doc コメント参照）、<see cref="IAsyncLifetime.InitializeAsync"/>
/// （= 本クラスの <see cref="CreateStoreAsync"/> 呼び出し元）から送出された
/// <see cref="SkipException"/> は Xunit.SkippableFact の <c>SkippableFactTestCase.RunAsync</c> が
/// コンストラクタ・<c>InitializeAsync</c>・テスト本体を包含する範囲で捕捉し、Skipped として報告する
/// （ソース確認済み。最終報告参照）。
/// </para>
/// <para>
/// <b>CI では確実に実行する（スキップでの green 偽装を防ぐ）</b>: GitHub Actions は既定で
/// 環境変数 <c>CI=true</c> を設定する（GitHub 公式ドキュメント "Variables reference" の
/// Default environment variables 表。確認日 2026-07-05）。この環境変数が設定されている場合、
/// 接続確認に失敗してもスキップせずそのまま例外を伝播させて fail する——CI で SQL Server の
/// 準備手順（ci.yml）が壊れているのにテストがスキップされ続けて green に見える事故を防ぐ
/// （依頼「CI 環境変数がある場合はスキップせず fail する設計を推奨」）。
/// </para>
/// </remarks>
public sealed class SqlServerLogStoreConformanceTests : LogStoreConformanceTestBase
{
    private string? _databaseName;

    protected override async Task<ILogStore> CreateStoreAsync()
    {
        var isAvailable = SqlServerTestConnection.IsAvailable();

        if (!isAvailable && !IsRunningInCi())
        {
            Skip.If(
                true,
                $"SQL Server に接続できないためスキップします（接続先: {SqlServerTestConnection.DescribeTarget()}）。" +
                $"環境変数 {SqlServerTestConnection.ConnectionStringEnvironmentVariable} に接続文字列を設定するか、" +
                "既定の LocalDB インスタンスを利用可能にしてください。");
        }

        // CI で isAvailable が false の場合はここでスキップせず先へ進み、以降の接続試行が
        // 例外を送出して素直に fail する（偽装 green を防ぐ。上記 doc コメント参照）。

        _databaseName = $"yagura_conformance_{Guid.NewGuid():N}";

        await using (var masterConnection = new SqlConnection(SqlServerTestConnection.GetMasterConnectionString()))
        {
            await masterConnection.OpenAsync().ConfigureAwait(false);

            await using var createCommand = masterConnection.CreateCommand();
            // データベース名はテスト内部で GUID から生成する制御下の値であり、利用者入力を
            // 経由しないため文字列連結で許容する（パラメータ化 DDL は SQL Server が
            // サポートしないため——CREATE DATABASE 名にパラメータバインドは使えない）。
            createCommand.CommandText = $"CREATE DATABASE [{_databaseName}];";
            await createCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var store = new SqlServerLogStore(SqlServerTestConnection.BuildConnectionString(_databaseName));
        await store.InitializeAsync().ConfigureAwait(false);
        return store;
    }

    protected override async Task DisposeStoreAsync(ILogStore store)
    {
        if (store is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }

        if (_databaseName is null)
        {
            return;
        }

        await using var masterConnection = new SqlConnection(SqlServerTestConnection.GetMasterConnectionString());
        await masterConnection.OpenAsync().ConfigureAwait(false);

        await using var dropCommand = masterConnection.CreateCommand();
        // SINGLE_USER + ROLLBACK IMMEDIATE: 接続プールに残った他接続があっても DROP を阻害しない
        // （Microsoft.Data.SqlClient の接続プーリングにより、Dispose 後も物理接続が残り得るため）。
        dropCommand.CommandText =
            $"""
            IF DB_ID(N'{_databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_databaseName}];
            END;
            """;
        await dropCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// GitHub Actions の既定環境変数 <c>CI</c>（確認日 2026-07-05。最終報告参照）で CI 実行かどうかを判定する。
    /// </summary>
    private static bool IsRunningInCi() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
}
