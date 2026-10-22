using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Tests;

public class TestCategoriesTests
{
    [Fact, Trait("Category", "Editor")]
    public void Every_test_in_this_assembly_has_a_category() =>
        TestCategories.MethodsWithoutCategory(typeof(TestCategoriesTests).Assembly).ShouldBeEmpty();
}
