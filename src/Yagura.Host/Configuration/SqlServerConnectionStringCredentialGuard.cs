namespace Yagura.Host.Configuration;

/// <summary>
/// <c>Storage:SqlServer:ConnectionString</c> 内の平文資格情報検出の挿入点（Issue #47。
/// configuration.md §2「DB 接続資格情報は設定ファイルに平文で置かない」の DPAPI 暗号化は
/// <b>本クラスでは実装しない</b>——検出の枠組みと呼び出し規約のみを用意する）。
/// </summary>
/// <remarks>
/// <para>
/// <b>スコープ（Issue #47 の依頼どおり）</b>: 「資格情報の DPAPI 暗号化は挿入点のみ」——
/// 実際の DPAPI 暗号化・復号（<c>System.Security.Cryptography.ProtectedData</c>、
/// <c>DataProtectionScope.LocalMachine</c>。ADR-0004 決定 5）は後続 Issue で実装する。
/// 本クラスは (1) 接続文字列に平文パスワードが含まれるかどうかの判定、(2) 判定結果を
/// 呼び出し側（<see cref="YaguraConfigurationLoader"/>・将来のウィザード保存経路）が
/// どう扱うべきかの規約、の 2 点のみを定義する。
/// </para>
/// <para>
/// <b>検出方法</b>: <see cref="Microsoft.Data.SqlClient.SqlConnectionStringBuilder"/> で
/// パースし、<c>IntegratedSecurity</c> が偽かつ <c>Password</c>（または <c>Pwd</c>）キーに
/// 非空の値がある場合を「平文資格情報あり」とする。DPAPI 暗号化表現（未実装）は将来、
/// 接続文字列全体または <c>Password</c> 値のみを Base64 等でラップした専用プレフィックス
/// （例: <c>dpapi:</c>）を持つ想定とし、本クラスはそのプレフィックスを持つ値を
/// 「既に暗号化済み」として平文検出の対象から除外する準備をコメントで明示する。
/// </para>
/// <para>
/// <b>本クラスが呼ばれていない現状</b>: <see cref="YaguraConfigurationLoader"/> は
/// 現時点でこのクラスを呼び出していない（起動時・再読み込み時の自動書き換え——
/// configuration.md §2「手編集で平文パスワードが書かれた場合、起動時および設定の
/// 再読み込み時に検出して暗号化表現へ書き換える」——は本 Issue の範囲外）。
/// 呼び出し配線は DPAPI 実装 Issue と同時に行う。
/// </para>
/// </remarks>
internal static class SqlServerConnectionStringCredentialGuard
{
    /// <summary>
    /// 将来の DPAPI 暗号化表現を示すプレフィックス（未実装。予約のみ）。
    /// </summary>
    internal const string EncryptedValuePrefix = "dpapi:";

    /// <summary>
    /// 接続文字列に平文の SQL 認証パスワードが含まれるかどうかを判定する。
    /// </summary>
    /// <param name="connectionString">判定対象の接続文字列（<c>null</c>/空文字は false を返す）。</param>
    public static bool ContainsPlaintextCredential(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        if (connectionString.StartsWith(EncryptedValuePrefix, StringComparison.Ordinal))
        {
            // 将来の暗号化表現（未実装）はここで除外する準備——実装時に復号経路を追加する。
            return false;
        }

        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            return !builder.IntegratedSecurity && !string.IsNullOrEmpty(builder.Password);
        }
        catch (ArgumentException)
        {
            // 接続文字列として不正な値の扱いは呼び出し側（YaguraConfigurationLoader の
            // 不正値 3 分類）に委ねる——本メソッドは「判定できない」を false として返す。
            return false;
        }
    }
}
