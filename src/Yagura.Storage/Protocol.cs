namespace Yagura.Storage;

/// <summary>
/// ログを受信したトランスポートプロトコル。
/// </summary>
public enum Protocol
{
    Udp,
    Tcp,

    /// <summary>
    /// syslog over TLS（RFC 5425。TCP 6514。opt-in。Issue #137）。DB へは新しい列挙値として
    /// 追記する——既存の Udp/Tcp の順序・整数値は変えない（additive-only。Sqlite/SqlServer とも
    /// <c>Protocol</c> 列は整数格納であり、既存データとの互換を保つ）。
    /// </summary>
    Tls,
}
