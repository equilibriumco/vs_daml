using System.Reflection;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests;

// The library has no code yet; this proves it builds and loads on both test frameworks.
public class AssemblyLoadTests
{
    [Fact, Trait("Category", "Unit")]
    public void Library_loads()
    {
        Assembly.Load("Daml.Build.Core").GetName().Name.ShouldBe("Daml.Build.Core");
    }
}
