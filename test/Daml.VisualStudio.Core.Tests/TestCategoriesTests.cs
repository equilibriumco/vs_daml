using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests;

public class TestCategoriesTests
{
    [Fact, Trait("Category", "Unit")]
    public void Every_test_in_this_assembly_has_a_category() =>
        TestCategories.MethodsWithoutCategory(typeof(TestCategoriesTests).Assembly).ShouldBeEmpty();
}
