using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Yagura.Abstractions.Administration;

namespace Yagura.Host.Administration;

/// <summary>
/// SQL Server 接続失敗の分類（database.md §6.1——原因別の次の一手の構造化）。
/// </summary>
/// <remarks>
/// <para>
/// 分類の根拠（Microsoft Learn 公式ドキュメント確認 2026-07-07。conventions.md
/// 「技術的主張の検証」）:
/// </para>
/// <list type="bullet">
/// <item>エラー 18456 = LOGON_FAILED「Login failed for user」（MSSQLSERVER_18456）。クライアント
/// へ返る情報は意図的に詳細を隠すため、誤パスワードと DB 不在（state 38/46）を区別できない
/// ——画面の案内が条件付きである理由。</item>
/// <item>エラー 4060 =「Cannot open database "%.*ls" requested by the login. The login
/// failed.」（Database Engine events and errors 4000-4999）。</item>
/// <item><c>SEC_E_UNTRUSTED_ROOT</c> = 0x80090325「The certificate chain was issued by an
/// authority that is not trusted.」（COM Error Codes (Security and Setup)）。TLS ハンドシェイク
/// 失敗の <see cref="SqlException"/> は Number では判別できず、内側の
/// <see cref="Win32Exception"/> の NativeErrorCode に現れる。</item>
/// </list>
/// <para>
/// 該当しない失敗は <see cref="PromotionConnectionFailureKind.Unclassified"/> に落とす——
/// 誤分類して間違った修復 SQL を断定提示するより、生メッセージ + 汎用案内に留める安全側
/// （database.md §6.1）。
/// </para>
/// </remarks>
internal static class SqlConnectionFailureClassifier
{
    /// <summary>SChannel: 証明書チェーンが信頼されていない機関によって発行された。</summary>
    private const int SecEUntrustedRoot = unchecked((int)0x80090325);

    // 到達不能を示す Winsock / システムエラーコード（SqlException.Number には SNI が内側の
    // OS エラーコードを転写する）: 53 = ネットワークパスが見つからない、2 = ファイルが
    // 見つからない（named pipe）、10060 = 接続タイムアウト、10061 = 接続拒否、
    // 11001 = ホストが見つからない、1225 = リモート側が接続を拒否、-2 = クライアント側
    // タイムアウト（Microsoft.Data.SqlClient のタイムアウト規約）、258 = 待機タイムアウト。
    private static readonly int[] UnreachableCodes = [53, 2, 10060, 10061, 11001, 1225, -2, 258];

    public static PromotionConnectionFailureKind Classify(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                var kind = ClassifySqlErrorNumber(sql.Number);
                if (kind != PromotionConnectionFailureKind.Unclassified)
                {
                    return kind;
                }
            }

            if (current is Win32Exception win32)
            {
                if (win32.NativeErrorCode == SecEUntrustedRoot)
                {
                    return PromotionConnectionFailureKind.CertificateNotTrusted;
                }

                if (UnreachableCodes.Contains(win32.NativeErrorCode))
                {
                    return PromotionConnectionFailureKind.ServerUnreachable;
                }
            }
        }

        return PromotionConnectionFailureKind.Unclassified;
    }

    /// <summary>
    /// SQL Server エラー番号による分類（<see cref="SqlException"/> はテストから生成できない
    /// ため、番号判定を分離して単体テストの対象にする）。
    /// </summary>
    internal static PromotionConnectionFailureKind ClassifySqlErrorNumber(int number) => number switch
    {
        18456 => PromotionConnectionFailureKind.LoginFailed,
        4060 => PromotionConnectionFailureKind.DatabaseNotFound,
        _ when UnreachableCodes.Contains(number) => PromotionConnectionFailureKind.ServerUnreachable,
        _ => PromotionConnectionFailureKind.Unclassified,
    };
}
