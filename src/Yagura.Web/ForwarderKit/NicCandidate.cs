namespace Yagura.Web.ForwarderKit;

/// <summary>
/// フォワーダキット生成画面の宛先候補 1 件（ADR-0008 設計条件 1・委任 #6）。
/// </summary>
/// <param name="Address">候補のアドレス（IPv4/IPv6 の文字列表現）。</param>
/// <param name="Description">NIC の説明名（NIC の <c>Name</c> + <c>Description</c>）。</param>
public sealed record NicCandidate(string Address, string Description);
