using System.Linq;
using System.Reflection;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

// The same check the other test assemblies get from TestHelpers; this project stays free of
// references to the extension and its helpers on purpose.
public class TestCategoriesTests
{
    [Fact, Trait("Category", "Integration")]
    public void Every_test_in_this_assembly_has_a_category()
    {
        var missing =
            from type in typeof(TestCategoriesTests).Assembly.GetTypes()
            from method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            where method.CustomAttributes.Any(a => typeof(FactAttribute).IsAssignableFrom(a.AttributeType))
            where !HasCategory(method) && !HasCategory(type)
            select type.FullName + "." + method.Name;

        Assert.Empty(missing);
    }

    private static bool HasCategory(MemberInfo member) =>
        member.CustomAttributes.Any(a => a.AttributeType == typeof(TraitAttribute) && Equals(a.ConstructorArguments[0].Value, "Category"));
}
