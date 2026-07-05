namespace Yagura.Host.Administration;

/// <summary>
/// 本番昇格の準備フェーズにおける SQL Server 接続検証の抽象（database.md §6.1。M8-4。Issue #71）。
/// </summary>
/// <remarks>
/// 抽象を挟む理由: 実際の SQL Server がない開発機・CI でも、本番昇格ウィザードの経路
/// （検証 → 選択 → 実行・監査・冪等性）をテスト実装で検証できる形にする（Issue #71 の要件。
/// SQL Server 適合テストと同様、実サーバに対する検証は環境変数で opt-in する統合テストの管轄）。
/// 実体は <see cref="SqlServerConnectionValidator"/>（<c>Yagura.Host</c> が DI で結線する）。
/// </remarks>
public interface ISqlServerConnectionValidator
{
    /// <summary>
    /// 接続文字列で SQL Server への接続を試み、結果を返す。<b>例外を投げない</b>——失敗は
    /// 結果（<see cref="SqlServerConnectionValidationResult.Success"/> = false + 利用者向け
    /// メッセージ）で返す。メッセージに接続文字列・パスワードを含めてはならない。
    /// </summary>
    Task<SqlServerConnectionValidationResult> ValidateAsync(
        string connectionString,
        CancellationToken cancellationToken = default);
}

/// <summary>接続検証の結果。</summary>
/// <param name="Success">接続に成功したか。</param>
/// <param name="Message">利用者向けの結果メッセージ（秘密情報を含めない）。</param>
public sealed record SqlServerConnectionValidationResult(bool Success, string Message);
