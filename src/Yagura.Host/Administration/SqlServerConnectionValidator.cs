using Microsoft.Data.SqlClient;

namespace Yagura.Host.Administration;

/// <summary>
/// <see cref="ISqlServerConnectionValidator"/> の実体（M8-4。Issue #71）。
/// </summary>
/// <remarks>
/// 接続を開いて <c>SELECT 1</c> を実行する最小の検証。失敗は
/// <see cref="SqlConnectionFailureClassifier"/> で原因を分類して返す（database.md §6.1 の
/// 原因別の次の一手）。スキーマ・権限の事前検証（database.md §5.2 の SQL 提示・§6.1
/// 準備フェーズの「接続・権限・スキーマの事前検証」の完全形）は切替本番の実行時手順と
/// 合わせて後続 Issue で拡充する——本骨格は「接続の成立」のみを検証の対象とする。
/// 接続試行の打ち切りは接続文字列の <c>Connect Timeout</c>（既定 15 秒）に従う——
/// 応答しないサーバで無限待ちにはならない。
/// </remarks>
public sealed class SqlServerConnectionValidator : ISqlServerConnectionValidator
{
    /// <inheritdoc/>
    public async Task<SqlServerConnectionValidationResult> ValidateAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return new SqlServerConnectionValidationResult(true, "接続に成功しました。");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException or FormatException)
        {
            // SqlException.Message はサーバ名・DB 名を含み得るが、いずれも管理者自身が入力した
            // 値であり秘密情報（パスワード）は SqlClient がメッセージに載せない。原因の要約と
            // して利用者向けにそのまま返す（監査記録には載せない——記録するのは成否と分類のみ。
            // PromotionWizardService 参照）。
            return new SqlServerConnectionValidationResult(
                false,
                $"接続できませんでした: {ex.Message}",
                SqlConnectionFailureClassifier.Classify(ex));
        }
    }
}
