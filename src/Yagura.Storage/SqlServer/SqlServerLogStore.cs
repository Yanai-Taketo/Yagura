using Microsoft.Data.SqlClient;

namespace Yagura.Storage.SqlServer;

/// <summary>
/// <see cref="ILogStore"/> の SQL Server 実装（database.md §5 本番 provider）。
/// </summary>
/// <remarks>
/// <para>
/// <b>読み書き分離の性質（database.md §1.2 契約表 末尾・§1.3 の文書化義務）</b>: 本実装は
/// 接続の分離レベルを明示的に変更せず、<b>SQL Server の既定分離レベル（READ COMMITTED、かつ
/// <c>READ_COMMITTED_SNAPSHOT</c> データベースオプションは既定 OFF）</b>のまま動作する。
/// この既定構成では、Microsoft Learn 公式ドキュメント「SET TRANSACTION ISOLATION LEVEL
/// (Transact-SQL)」の記載どおり:
/// <c>"If READ_COMMITTED_SNAPSHOT is set to OFF (the default on SQL Server), the Database Engine
/// uses shared locks to prevent other transactions from modifying rows while the current
/// transaction is running a read operation. The shared locks also block the statement from
/// reading rows modified by other transactions until the other transaction is completed."</c>
/// —— <b>つまり読み取り（検索）と書き込み（バッチ挿入・保持期間削除）は互いにブロックし得る</b>。
/// これは SQLite（WAL。<see cref="Sqlite.SqliteLogStore"/> のドキュメント参照）が実現する
/// 「読み取りは書き込みをブロックせず、書き込みも読み取りをブロックしない」性質とは<b>明確に異なる</b>。
/// </para>
/// <para>
/// <b>この性質の含意</b>: 対話的検索（<see cref="QueryAsync"/>）が長時間の共有ロックを保持すると、
/// 同時に実行される <see cref="WriteBatchAsync"/>・<see cref="DeleteOlderThanAsync"/> がブロックされ得る
/// （逆方向も同様）。<see cref="LogQuery.Timeout"/> による上限時間は「検索自体の打ち切り」であり、
/// 「検索が他の操作をブロックする時間」を直接には制限しない——ロック保持は検索文の実行時間と
/// 概ね一致するため、対話的検索のタイムアウト設計（M-10）が実質的な上限を与える。
/// <c>READ_COMMITTED_SNAPSHOT</c>（行バージョニングでロック不要の読み取りを実現する DB オプション）を
/// 有効化すれば SQLite の WAL に近い挙動へ変更できるが、v0.1 時点では既定のまま採用する
/// （行バージョニングは tempdb 使用量増加という別のトレードオフを伴うため、有効化の要否は
/// 実測を経て再評価する——DB-4 の実機検証と合わせて評価する候補とする）。
/// </para>
/// <para>
/// <b>付随する運用特性</b>: バルク挿入・保持期間削除は複数行にまたがるため、行ロックがページ/テーブル
/// ロックへエスカレーションし得る（SQL Server のロックエスカレーションの一般的な挙動。バッチサイズを
/// 適度に抑える設計——<see cref="RetentionConstants.DeleteBatchMaxSize"/> と同じ粒度——がエスカレーションの
/// 発生を抑える）。
/// </para>
/// </remarks>
public sealed partial class SqlServerLogStore : ILogStore, IBulkLogReader, IAsyncDisposable
{
    /// <summary>
    /// 現行のスキーマバージョン（<see cref="Sqlite.SqliteLogStore.CurrentSchemaVersion"/> と同じ意味）。
    /// v2（database.md §5.4）: 絞り込み列の複合索引の追加、
    /// ヘッダ列（Hostname/AppName/ProcId/MsgId）の NVARCHAR(MAX) 化、対象 NVARCHAR 列への
    /// COLLATE <see cref="SearchCollation"/> の明示を行う。
    /// </summary>
    internal const int CurrentSchemaVersion = 2;

    /// <summary>
    /// 自由文検索の一致規則（database.md §1.2 DB-6・§5.4）を実装する列単位 COLLATE。
    /// Windows 照合順序 version 100・大文字小文字非区別（CI）・アクセント区別（AS）・
    /// かな種区別（KS）・全角/半角区別（WS）・補助文字対応（SC）。§5.4 の却下案検討を経て確定
    /// （KS/WS を明示し、大文字小文字以外を区別する側に固定することで「折り畳むのは大文字小文字のみ」
    /// という DB-6 の規則を過不足なく実装する）。
    /// </summary>
    internal const string SearchCollation = "Latin1_General_100_CI_AS_KS_WS_SC";

    // SERVERPROPERTY('EngineEdition') の値（Microsoft Learn "SERVERPROPERTY (Transact-SQL)" の
    // Edition テーブル）: "4 = Express (For Express, Express with Tools, and
    // Express with Advanced Services)"。
    private const int EngineEditionExpress = 4;

    private readonly string _connectionString;

    // Windows 統合認証の接続か（イベントログ警告 1031 の分類は統合認証の接続に限って行う——
    // ADR-0015 決定 5。SQL 認証の 18456/4060 は統合認証の切り分け対象ではない）。
    private readonly bool _integratedAuthentication;

    /// <summary>
    /// 指定した接続文字列で <see cref="SqlServerLogStore"/> を構築する。
    /// </summary>
    /// <param name="connectionString">
    /// SQL Server への接続文字列（configuration.md §2 の DPAPI 保護対象。本クラスは
    /// 復号済みの平文接続文字列を受け取る——復号自体はホスト層の責務）。
    /// </param>
    public SqlServerLogStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _integratedAuthentication = UsesIntegratedAuthentication(connectionString);
    }

    /// <summary>
    /// 接続文字列が Windows 統合認証か。パース不能な接続文字列はどのみち接続時に失敗して
    /// 通常の恒久障害経路（1030）に乗るため、ここでは偽へ倒すだけにする（構築時に投げない——
    /// 本コンストラクタは従来から接続文字列の妥当性検証を責務にしていない）。
    /// </summary>
    private static bool UsesIntegratedAuthentication(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString).IntegratedSecurity;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Microsoft.Data.SqlClient も既定で接続プーリングを行う。SqliteLogStore と平行に、
        // 明示的な破棄経路を用意する（テスト・退避処理でのプール解放を安全にするため）。
        // ClearPool は未オープンの SqlConnection インスタンスを鍵として渡せば足りるが、
        // そのインスタンス自体は使い捨てのため確実に破棄する（using で漏れを防ぐ）。
        using var connection = new SqlConnection(_connectionString);
        SqlConnection.ClearPool(connection);
        return ValueTask.CompletedTask;
    }
}
