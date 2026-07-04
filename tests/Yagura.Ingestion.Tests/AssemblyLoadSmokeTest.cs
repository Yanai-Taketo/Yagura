using System.Reflection;

namespace Yagura.Ingestion.Tests;

public class AssemblyLoadSmokeTest
{
    [Fact]
    public void YaguraIngestionAssembly_CanBeLoaded()
    {
        var assembly = Assembly.Load("Yagura.Ingestion");

        Assert.NotNull(assembly);
    }
}
