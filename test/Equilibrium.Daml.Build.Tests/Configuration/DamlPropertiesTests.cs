using System.Globalization;
using System.Linq;
using Equilibrium.Daml.Build.Core.Configuration;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Configuration;

public class DamlPropertiesTests
{
    [Fact, Trait("Category", "Unit")]
    public void Every_property_is_listed_once_under_the_Daml_prefix()
    {
        var names = DamlProperties.All.Select(p => p.Name).ToList();

        names.ShouldBeUnique();
        names.ShouldAllBe(n => n.StartsWith("Daml"));
        typeof(DamlProperties).GetProperties().Length.ShouldBe(DamlProperties.All.Count + 1, "a property missing from All");
    }

    // A default the reader itself rejects would turn an unset property into an error.
    [Fact, Trait("Category", "Unit")]
    public void Every_default_is_a_value_the_property_accepts()
    {
        foreach (var property in DamlProperties.All.Where(p => p.Allowed == "true or false"))
        {
            MSBuildValue.Bool(property.Default, defaultValue: false).ShouldNotBeNull(property.Name);
        }
        int.Parse(DamlProperties.ProcessTimeoutSeconds.Default, CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
        int.Parse(DamlProperties.LockTimeoutSeconds.Default, CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
        CodegenComponentPin.TryParse(DamlProperties.CodegenComponent.Default).ShouldNotBeNull();
    }
}
