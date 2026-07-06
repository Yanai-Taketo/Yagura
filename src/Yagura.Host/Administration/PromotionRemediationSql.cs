using Yagura.Abstractions.Administration;

namespace Yagura.Host.Administration;

/// <summary>
/// 検証失敗時に提示する修復 SQL の生成（database.md §5.2「権限不足時の自走可能な失敗」・
/// §6.1 の原因別の次の一手）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>提示のみでありサーバは実行しない</b>（§5.2 の統治——DBA 依頼文としての流通を
/// 前提とする）。</item>
/// <item><b>秘密情報を含まない</b>: SQL 認証のパスワードは明示のプレースホルダとし、生成時点で
/// 実値を埋めない（§5.2——依頼文がメール・チケットを経由して流通する前提）。</item>
/// <item><b>db_owner は導入時権限</b>であることを SQL 内コメントで明記する（実行時最小権限への
/// 分離——§5.2 の完全形——は後続 Issue。ペルソナレビュー PR #102 田中/クリスの指摘）。</item>
/// </list>
/// </remarks>
internal static class PromotionRemediationSql
{
    /// <summary>SQL 認証のパスワード部のプレースホルダ（実値を埋めない——database.md §5.2）。</summary>
    internal const string PasswordPlaceholder = "パスワードをここに入力";

    /// <summary>
    /// ログイン失敗（18456）向け: ログイン作成 + ユーザー作成 + 権限付与。
    /// 18456 は誤パスワードでも返るため、画面側の案内は「未作成の場合」の条件付き提示とする
    /// （database.md §6.1）——SQL 冒頭のコメントにも条件を残す。
    /// </summary>
    public static string ForLoginFailed(
        PromotionAuthenticationMode authenticationMode,
        string loginName,
        string databaseName)
    {
        var login = EscapeIdentifier(loginName);
        var createLogin = authenticationMode == PromotionAuthenticationMode.WindowsIntegrated
            ? $"CREATE LOGIN [{login}] FROM WINDOWS;"
            : $"CREATE LOGIN [{login}] WITH PASSWORD = N'<{PasswordPlaceholder}>';";

        return
            $"""
            -- Yagura の接続用ログインを作成します（ログインが未作成の場合に実行してください）。
            {createLogin}
            GO
            {GrantDatabaseAccess(login, databaseName)}
            """;
    }

    /// <summary>
    /// データベース不在（4060）向け: DB 作成 + ユーザー作成 + 権限付与
    /// （4060 はログイン自体は成立している——database.md §6.1）。
    /// </summary>
    public static string ForDatabaseNotFound(string loginName, string databaseName)
    {
        var login = EscapeIdentifier(loginName);
        return
            $"""
            -- Yagura 用のデータベースを作成します（データベースが未作成の場合に実行してください）。
            CREATE DATABASE [{EscapeIdentifier(databaseName)}];
            GO
            {GrantDatabaseAccess(login, databaseName)}
            """;
    }

    private static string GrantDatabaseAccess(string escapedLogin, string databaseName)
    {
        var database = EscapeIdentifier(databaseName);
        return
            $"""
            USE [{database}];
            -- db_owner は導入時（スキーマ作成を含む）の権限です。実行時最小権限への分離で縮小予定です。
            CREATE USER [{escapedLogin}] FOR LOGIN [{escapedLogin}];
            ALTER ROLE [db_owner] ADD MEMBER [{escapedLogin}];
            GO
            """;
    }

    /// <summary>角括弧区切り識別子のエスケープ（<c>]</c> → <c>]]</c>）。</summary>
    private static string EscapeIdentifier(string name) => name.Replace("]", "]]", StringComparison.Ordinal);
}
