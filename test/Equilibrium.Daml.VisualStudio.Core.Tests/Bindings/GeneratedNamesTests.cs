using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Bindings;

public class GeneratedNamesTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("Point_u0027", "Point'")]
    [InlineData("Point__u0027", "Point_u0027")]
    [InlineData("Point___x", "Point__x")]
    [InlineData("_u0031Name", "1Name")]
    [InlineData("@event", "event")]
    public void Emitted_identifiers_recover_the_source_spelling(string emitted, string source) =>
        GeneratedNames.FromCSharp(emitted).ShouldBe(source);
}
