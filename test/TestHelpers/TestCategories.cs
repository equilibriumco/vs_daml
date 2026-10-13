using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Daml.TestHelpers;

/// <summary>
/// The test scripts select tests by their <c>Category</c> trait, so a test without one is
/// never run by any gate. Each test assembly asserts that it has none.
/// </summary>
public static class TestCategories
{
    /// <summary>The test methods of <paramref name="assembly"/> with no <c>Category</c> trait on the method or its class.</summary>
    public static IReadOnlyList<string> MethodsWithoutCategory(Assembly assembly)
    {
        var result = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsTest(method) && !HasCategory(method) && !HasCategory(type))
                {
                    result.Add(type.FullName + "." + method.Name);
                }
            }
        }
        result.Sort(System.StringComparer.Ordinal);
        return result;
    }

    // Fact, Theory and everything derived from them (Property, SkippableFact, IdeFact, ...).
    private static bool IsTest(MethodInfo method) =>
        method.CustomAttributes.Any(a => typeof(FactAttribute).IsAssignableFrom(a.AttributeType));

    private static bool HasCategory(MemberInfo member) =>
        member.CustomAttributes.Any(a => a.AttributeType == typeof(TraitAttribute) && Equals(a.ConstructorArguments[0].Value, "Category"));
}
