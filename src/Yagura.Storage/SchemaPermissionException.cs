namespace Yagura.Storage;

/// <summary>
/// スキーマ管理（新規作成・版間移行）が権限不足で失敗したことを表す例外
/// （database.md §1.2 契約 1・§5.2）。
/// </summary>
/// <remarks>
/// <para>
/// database.md §5.2 は「不足内容と、管理者がそのまま実行できる SQL を区別可能に報告する」ことを
/// 要求する。本例外は provider 共通のインターフェース上の表明であり、<see cref="MissingPermission"/>
/// （不足内容）と <see cref="RemediationSql"/>（実行可能な SQL。秘密情報を含まない——§5.2）を
/// 分離して保持する。
/// </para>
/// <para>
/// <b>SQLite での実際の発生可能性</b>: SQLite はファイルシステム上の単一ファイルであり、
/// 「ログイン作成・権限付与」という区別可能な権限体系を持たない（読み書き不可はファイル ACL の
/// 単純な可否のみで、原因の细分化した報告は SQLite 自体からは得られない）。そのため
/// <see cref="SqliteLogStore"/> は本例外を実質的に送出しない——ファイル ACL 不足は
/// <see cref="LogStoreWriteException"/>（<see cref="LogStoreFailureKind.Permanent"/>）として
/// 報告する。本例外型はインターフェース契約として定義し、SQL Server 実装（M5-3）で実体化する。
/// </para>
/// </remarks>
public sealed class SchemaPermissionException : Exception
{
    /// <summary>
    /// 不足している権限の内容（人間可読な説明）。
    /// </summary>
    public string MissingPermission { get; }

    /// <summary>
    /// 管理者がそのまま実行できる SQL（環境の実値を埋め込むが、秘密情報は含まない。
    /// パスワード等が必要な箇所は明示のプレースホルダとする——database.md §5.2）。
    /// </summary>
    public string RemediationSql { get; }

    public SchemaPermissionException(string missingPermission, string remediationSql)
        : base($"スキーマ管理に必要な権限が不足しています: {missingPermission}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(missingPermission);
        ArgumentException.ThrowIfNullOrWhiteSpace(remediationSql);

        MissingPermission = missingPermission;
        RemediationSql = remediationSql;
    }
}
