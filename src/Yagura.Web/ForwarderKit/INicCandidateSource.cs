namespace Yagura.Web.ForwarderKit;

/// <summary>
/// フォワーダキット生成画面の宛先候補検出の契約（ADR-0008 設計条件 1・委任 #6）。
/// 実体（<see cref="SystemNicCandidateSource"/>）は OS の NIC 列挙に依存するため、
/// 画面・テストからは本契約経由で差し替えられるようにする。
/// </summary>
public interface INicCandidateSource
{
    /// <summary>
    /// 候補となるアドレスを列挙する。除外条件（ループバック・リンクローカル/APIPA・
    /// 無効化された NIC）は <see cref="NicCandidateFilter"/> が判定する（ADR-0008 設計条件 1）。
    /// </summary>
    IReadOnlyList<NicCandidate> GetCandidates();
}
