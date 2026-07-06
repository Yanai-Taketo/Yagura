namespace Yagura.Web.ReverseDns;

/// <summary>
/// 逆引き（PTR）ホスト名表示の設定（ADR-0007 決定 4。設定キー <c>Viewer:ReverseDns:Enabled</c>）。
/// Host が設定読み込みの結果から構築して DI へ登録する（<c>YaguraAdminListenerPort</c> と同じ
/// 受け渡しパターン）。
/// </summary>
/// <param name="Enabled">
/// 逆引き解決の有効/無効。無効時は DNS クエリを一切発せず、キャッシュ済みの名前の表示も行わない
/// （「オフ = 逆引き名は出ない」を単純に保つ——ADR-0007 決定 4）。
/// </param>
public sealed record ReverseDnsDisplayOptions(bool Enabled);
