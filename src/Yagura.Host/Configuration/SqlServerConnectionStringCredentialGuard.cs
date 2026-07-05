namespace Yagura.Host.Configuration;

/// <summary>
/// <c>Storage:SqlServer:ConnectionString</c> 内の平文資格情報の検出（M5-3 / Issue #47 で
/// 検出の枠組みを挿入、DPAPI 暗号化の実装と同時に <see cref="YaguraConfigurationLoader"/> へ
/// 配線した。configuration.md §2）。
/// </summary>
/// <remarks>
/// <para>
/// <b>役割分担</b>: 実際の DPAPI 暗号化・復号は <see cref="DpapiConnectionStringProtector"/>
/// （<c>System.Security.Cryptography.ProtectedData</c>、<c>DataProtectionScope.LocalMachine</c> +
/// 固有 entropy。ADR-0004 決定 5）が担う。本クラスは (1) 接続文字列に平文パスワードが
/// 含まれるかどうかの判定、(2) 判定結果を呼び出し側がどう扱うべきかの規約、の 2 点を定義する。
/// </para>
/// <para>
/// <b>検出方法</b>: <see cref="Microsoft.Data.SqlClient.SqlConnectionStringBuilder"/> で
/// パースし、<c>IntegratedSecurity</c> が偽かつ <c>Password</c>（または <c>Pwd</c>）キーに
/// 非空の値がある場合を「平文資格情報あり」とする。DPAPI 暗号化表現
/// （<see cref="DpapiConnectionStringProtector.Prefix"/> 付き）は「既に暗号化済み」として
/// 平文検出の対象から除外する。
/// </para>
/// <para>
/// <b>検出後の扱い（configuration.md §2。2026-07-06 オーナー決定）</b>: 手編集で書かれた
/// 平文の接続文字列は従来どおり<b>受理する</b>（手編集ユーザーを壊さない）。資格情報入りの
/// 平文には強い警告を出し続けるが、<b>設定ファイルの自動書き換え（平文 → 暗号化への書き戻し）は
/// 行わない</b>——利用者のファイルを勝手に変更しない方針。暗号化表現への移行はウィザード
/// （昇格）経由の再入力で行う。
/// </para>
/// </remarks>
internal static class SqlServerConnectionStringCredentialGuard
{
    /// <summary>
    /// DPAPI 暗号化表現を示すプレフィックス（実体は <see cref="DpapiConnectionStringProtector.Prefix"/>）。
    /// </summary>
    internal const string EncryptedValuePrefix = DpapiConnectionStringProtector.Prefix;

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
            // 暗号化表現は「既に暗号化済み」として平文検出の対象から除外する
            // （復号経路は YaguraConfigurationLoader → DpapiConnectionStringProtector）。
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
