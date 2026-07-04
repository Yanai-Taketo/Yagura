using System.Reflection;

namespace Yagura.Web.Tests;

public class AssemblyLoadSmokeTest
{
    [Fact]
    public void YaguraWebAssembly_CanBeLoaded()
    {
        var assembly = Assembly.Load("Yagura.Web");

        Assert.NotNull(assembly);
    }
}
