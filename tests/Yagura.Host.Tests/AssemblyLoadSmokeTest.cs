using System.Reflection;

namespace Yagura.Host.Tests;

public class AssemblyLoadSmokeTest
{
    [Fact]
    public void YaguraHostAssembly_CanBeLoaded()
    {
        var assembly = Assembly.Load("Yagura.Host");

        Assert.NotNull(assembly);
    }
}
