using System.Net.NetworkInformation;

namespace Yagura.Web.ForwarderKit;

/// <summary>
/// OS の NIC 列挙による宛先候補検出の実体（ADR-0008 設計条件 1・委任 #6）。
/// 除外判定そのものは <see cref="NicCandidateFilter"/>（純粋関数）に委譲し、本クラスは
/// <see cref="NetworkInterface.GetAllNetworkInterfaces"/> の列挙・説明名の組み立てのみを担う。
/// </summary>
public sealed class SystemNicCandidateSource : INicCandidateSource
{
    /// <inheritdoc/>
    public IReadOnlyList<NicCandidate> GetCandidates()
    {
        var candidates = new List<NicCandidate>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var description = FormatDescription(nic);

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;

                if (!NicCandidateFilter.IsCandidate(nic.NetworkInterfaceType, nic.OperationalStatus, address))
                {
                    continue;
                }

                candidates.Add(new NicCandidate(address.ToString(), description));
            }
        }

        return candidates;
    }

    /// <summary>NIC の説明名 = NIC の <c>Name</c> + <c>Description</c>(ADR-0008 設計条件 1)。</summary>
    private static string FormatDescription(NetworkInterface nic) =>
        nic.Name == nic.Description ? nic.Name : $"{nic.Name} ({nic.Description})";
}
