using System;
using System.IO;
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
            from type in LoadableTypes()
            from method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            where method.CustomAttributes.Any(a => typeof(FactAttribute).IsAssignableFrom(a.AttributeType))
            where !HasCategory(method) && !HasCategory(type)
            select type.FullName + "." + method.Name;

        Assert.Empty(missing);
    }

    // The helpers that drive the results window hold WebView2 types, and WebView2 comes with
    // Visual Studio, not with this project, so outside Visual Studio those helpers don't load.
    // The test classes do; anything else that doesn't load is a real problem.
    private static Type[] LoadableTypes()
    {
        try
        {
            return typeof(TestCategoriesTests).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var unexpected = ex.LoaderExceptions
                .Where(e => !(e is FileNotFoundException missing && (missing.FileName ?? "").StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal)))
                .Select(e => e.Message);
            Assert.Empty(unexpected);
            return ex.Types.Where(t => t is not null).ToArray()!;
        }
    }

    private static bool HasCategory(MemberInfo member) =>
        member.CustomAttributes.Any(a => a.AttributeType == typeof(TraitAttribute) && Equals(a.ConstructorArguments[0].Value, "Category"));
}
