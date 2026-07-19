using System.Security.Cryptography;

namespace Yagura.Host.Configuration;

/// <summary>
/// <c>Storage:SqlServer:ConnectionString</c> の DPAPI 暗号化・復号（ADR-0004 決定 5
/// 「v0.1: DPAPI 完動」。configuration.md §2。2026-07-06 オーナー決定による公開前実装）。
/// </summary>
/// <remarks>
/// <para>
/// <b>表現形式</b>: <c>dpapi:&lt;Base64&gt;</c>（接頭辞 <see cref="Prefix"/> + 暗号文の Base64）。
/// 接頭辞により平文の接続文字列と機械判別できる（M5-3 の
/// <see cref="SqlServerConnectionStringCredentialGuard.EncryptedValuePrefix"/> で予約済みの形式を
/// そのまま採用した）。
/// </para>
/// <para>
/// <b>スコープ: <see cref="DataProtectionScope.LocalMachine"/> + 固有 entropy</b>。採用理由:
/// 暗号化（昇格ウィザード）・復号（起動時の設定読み込み）はいずれもサービスプロセス内で
/// 行われるが、管理者が手動でサービスアカウントを変更しても復号が壊れない machine スコープが
/// 運用上の定石である（CurrentUser スコープはアカウント変更・プロファイル削除で復号不能になる）。
/// 帰結として<b>暗号化データは当該マシンに束縛される</b>——<c>yagura.json</c> を他マシンへ
/// コピーしても復号できず、読み込み側は SQLite への縮小 + 強い警告で継続する
/// （<see cref="YaguraConfigurationLoader"/>。ADR-0004 決定 5 が明記する制約と同じ帰結）。
/// </para>
/// <para>
/// <b>固有 entropy の位置づけ</b>: entropy は OSS のソースコードに含まれるため秘密ではない。
/// 目的は (1) 同一マシン上の無関係なプロセスが entropy なしの
/// <c>ProtectedData.Unprotect(data, null, LocalMachine)</c> で機械的に復号することの防止
/// （名前空間の分離であり、アクセス制御の主体はデータルートの ACL——ADR-0004 決定 5）、
/// (2) 値の末尾 <c>:v1</c> による表現形式の版管理（将来、暗号化方式を変える場合は
/// 接頭辞または entropy の版を上げて共存させる）の 2 点である。
/// </para>
/// <para>
/// <b>復号失敗の扱い</b>: 改ざん・他マシンで暗号化された値・Base64 として不正な値はいずれも
/// <see cref="TryUnprotect"/> が <see langword="false"/> を返す。呼び出し側
/// （<see cref="YaguraConfigurationLoader"/>）は M5-3 の「接続文字列不備」と同じ
/// 「SQLite へ縮小 + 強い警告」（configuration.md §1 の縮小側継続——起動を止めない）を適用する。
/// </para>
/// </remarks>
internal static class DpapiConnectionStringProtector
{
    /// <summary>
    /// 暗号化表現を示す接頭辞（M5-3 の <c>SqlServerConnectionStringCredentialGuard</c> で
    /// 予約された値。平文接続文字列は SqlClient のキー <c>キー=値;</c> 形式であり、
    /// この接頭辞と衝突しない）。
    /// </summary>
    internal const string Prefix = DpapiSecretProtector.Prefix;

    /// <summary>
    /// 固有 entropy（クラス remarks 参照。値を変更すると既存の暗号化表現が全て復号不能になる
    /// ため、変更は表現形式の版上げとしてのみ行うこと）。
    /// </summary>
    private const string EntropyNamespace = "Yagura:Storage:SqlServer:ConnectionString:v1";

    /// <summary>
    /// 値が暗号化表現（<see cref="Prefix"/> 付き）かどうかを判別する。
    /// </summary>
    internal static bool IsProtected(string? value) => DpapiSecretProtector.IsProtected(value);

    /// <summary>
    /// 平文の接続文字列を DPAPI（LocalMachine + 固有 entropy）で暗号化し、
    /// <c>dpapi:&lt;Base64&gt;</c> 表現を返す。
    /// </summary>
    /// <param name="plaintext">平文の接続文字列（null/空白は不可）。</param>
    /// <exception cref="PlatformNotSupportedException">Windows 以外の OS で呼ばれた場合。</exception>
    /// <exception cref="CryptographicException">DPAPI の暗号化自体が失敗した場合。</exception>
    internal static string Protect(string plaintext) =>
        DpapiSecretProtector.Protect(plaintext, EntropyNamespace);

    /// <summary>
    /// 暗号化表現を復号する。接頭辞なし・Base64 不正・DPAPI 復号失敗（改ざん・別マシンへの
    /// コピー）のいずれも <see langword="false"/> を返す（例外を漏らさない——呼び出し側の
    /// 「SQLite へ縮小 + 強い警告」に委ねる）。
    /// </summary>
    /// <param name="value">判定・復号する値。</param>
    /// <param name="plaintext">復号に成功した場合の平文。失敗時は <see langword="null"/>。</param>
    internal static bool TryUnprotect(string? value, out string? plaintext) =>
        DpapiSecretProtector.TryUnprotect(value, EntropyNamespace, out plaintext);
}
