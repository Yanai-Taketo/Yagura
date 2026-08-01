using Yagura.Web.ForwarderKit;

namespace Yagura.TestSupport.Fakes;

/// <summary>
/// <see cref="IForwarderMsiSource"/>のフェイク。<paramref name="lookup"/>を全アーキテクチャ共通で返す。
/// </summary>
public sealed class FakeForwarderMsiSource(string folderPath, ForwarderMsiLookup lookup) : IForwarderMsiSource
{
    public string FolderPath => folderPath;

    public ForwarderMsiLookup Lookup(ForwarderMsiArchitecture architecture) => lookup;
}
