using Yagura.Abstractions.Auditing;

namespace Yagura.Host.Observability.Auditing;

/// <summary>
/// 記録が監査記録の正本（アプリ記録ファイル）に載ったことを確かめられる書き込み口（security.md §4.2）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IAuditRecorder.RecordAsync"/> は成否を隠す（ADR-0004 決定 7——監査記録の書き込み不能は要求処理を
/// 妨げない）。一方、旧・組み込み DB ファイルの処分（database.md §6.3）は急ぐ理由のない不可逆な後始末であり、
/// 削除の前の記録が正本に載ったことを確かめてから進む——その確認だけに使う。
/// </para>
/// <para>
/// 正本に載らなかった事象は、障害中の保持・復旧後の書き戻しの対象にせず、イベントログへの併記も行わない
/// （呼び出し側はその操作を行わないため、行われなかった操作の記録を後から残さない）。例外は投げない。
/// </para>
/// </remarks>
public interface IConfirmedAuditRecorder : IAuditRecorder
{
    /// <summary>
    /// 事象を記録し、正本に載ったら <see langword="true"/> を返す。載らなければ何も残さずに
    /// <see langword="false"/> を返す。
    /// </summary>
    Task<bool> TryRecordConfirmedAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
