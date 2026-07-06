namespace Yagura.Web.ReverseDns;

/// <summary>
/// 送信元 IP アドレス → 逆引き（PTR）ホスト名の解決・キャッシュ（ADR-0007 決定 2）。
/// 閲覧層の読み取り専用サービスであり、サーバ状態を変更しない
/// （<c>IYaguraWriteService</c> ではない——security.md §1 L-5 の参照分離検査と両立）。
/// </summary>
public interface IReverseDnsResolver
{
    /// <summary>
    /// キャッシュ済みの表示名を返す。未解決なら <c>null</c> を返しつつ非同期解決を予約する
    /// （描画は解決を待たない——ADR-0007 決定 2）。機能オフ・対象帯域外・IP として不正な
    /// 値は常に <c>null</c>（解決の予約もしない）。
    /// </summary>
    /// <param name="sourceAddress">送信元アドレス（<c>LogRecord.SourceAddress</c> の文字列表現）。</param>
    /// <returns>無害化済み（RFC 1123 LDH・253 文字以内）の逆引きホスト名。なければ <c>null</c>。</returns>
    string? TryGetDisplayName(string sourceAddress);

    /// <summary>
    /// 非同期解決の完了が表示へ反映できる状態になったことの通知（束ね済み——解決完了
    /// 1 件ごとではなく一定間隔でまとめて発火する。ADR-0007 決定 2 の反映粒度）。
    /// 購読側（表示コンポーネント）はこの通知で <see cref="TryGetDisplayName"/> を再評価する。
    /// </summary>
    event Action? NamesUpdated;
}
