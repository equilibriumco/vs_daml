using System.Reflection;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests;

// The library has no code yet; this proves it builds and loads on both test frameworks.
public class AssemblyLoadTests
{
    [Fact, Trait("Category", "Unit")]
    public void Library_loads()
    {
        Assembly.Load("Daml.VisualStudio.LanguageServer").GetName().Name.ShouldBe("Daml.VisualStudio.LanguageServer");
    }
}
