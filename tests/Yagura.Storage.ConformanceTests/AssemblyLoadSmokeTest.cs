using System.Reflection;

namespace Yagura.Storage.ConformanceTests;

public class AssemblyLoadSmokeTest
{
    [Fact]
    public void YaguraStorageAssembly_CanBeLoaded()
    {
        var assembly = Assembly.Load("Yagura.Storage");

        Assert.NotNull(assembly);
    }
}
