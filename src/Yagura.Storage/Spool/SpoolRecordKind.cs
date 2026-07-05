namespace Yagura.Storage.Spool;

/// <summary>
/// スプールへ書くレコードの種別（architecture.md §3.2.1 の構造要件「レコード種別の識別
/// （通常ログ / 自己検証用の合成レコード。§3.2.5）」）。
/// </summary>
/// <remarks>
/// drain はこの種別を見て、<see cref="SelfTest"/> を DB 書き込みの直前で破棄する
/// （§3.2.5。証跡 DB への合成レコード混入を防ぐ）。この識別・破棄は定期自己検証時に
/// 限らず drain の常時動作である。
/// </remarks>
public enum SpoolRecordKind : byte
{
    /// <summary>受信した実際の syslog ログ。</summary>
    Normal = 0,

    /// <summary>定期自己検証（§3.2.5）が書き込む合成レコード。drain は DB へ書かず破棄する。</summary>
    SelfTest = 1,
}
