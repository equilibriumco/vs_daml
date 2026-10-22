using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.MSBuild;

// The checked-in generator output the binding index is tested against, held to what the pinned
// generator writes today. With EQDAML_UPDATE_BINDING_FIXTURES=1 it is rewritten instead.
public sealed class BindingFixtureTests : IDisposable
{
    private readonly MSBuildSandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    [InlineData("kinds", "Fixture")]
    [InlineData("names", "Names")]
    public void The_fixture_is_what_the_pinned_generator_writes(string fixtureName, string prefix)
    {
        var fixture = Path.Combine(Repository.Root, @"test\fixtures\bindings", fixtureName);
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var java = RequiredTools.Require(ExternalTool.Java);
        var dotnet = RequiredTools.Require(ExternalTool.Dotnet);
        _sandbox.UseSdkFeed();
        _sandbox.Write(@"contracts\daml.yaml", File.ReadAllText(Path.Combine(fixture, "daml.yaml")));
        foreach (var file in Directory.GetFiles(Path.Combine(fixture, "daml"), "*.daml", SearchOption.AllDirectories))
        {
            _sandbox.Write(Path.Combine("contracts", file.Substring(fixture.Length + 1)), File.ReadAllText(file));
        }
        _sandbox.Write(@"contracts\Contracts.msbuildproj", $"<Project Sdk=\"Equilibrium.Daml.Sdk/{SdkFeed.Version}\" />\n");
        _sandbox.Write(@"App\App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Equilibrium.Daml.Build.CSharp" Version="{SdkFeed.Version}" PrivateAssets="all" />
                <PackageReference Include="Daml.Runtime" Version="{SdkFeed.RuntimeVersion}" />
                <PackageReference Include="Daml.Ledger.Abstractions" Version="{SdkFeed.RuntimeVersion}" />
                <EqDamlReference Include="..\contracts\Contracts.msbuildproj" Namespace="{prefix}" />
              </ItemGroup>
            </Project>
            """);
        var properties = new Dictionary<string, string>
        {
            ["EqDamlDpmPath"] = dpm,
            ["EqDamlJavaHome"] = Path.GetDirectoryName(Path.GetDirectoryName(java))!,
            // Installs the generator once on a machine that hasn't got it yet.
            ["EqDamlAllowToolInstall"] = "true",
        };

        var build = _sandbox.Build(MSBuildHost.Dotnet, dotnet, @"App\App.csproj", properties: properties);

        build.ExitCode.ShouldBe(0, build.Log);
        var generation = Directory.GetDirectories(Path.Combine(_sandbox.Root, @"App\obj\Debug\net10.0\Daml")).ShouldHaveSingleItem();
        var provenance = GenerationManifest.Read(Path.Combine(generation, GenerationManifest.FileName))!.Package!;
        if (fixtureName == "names")
        {
            provenance.Modules.ShouldBe(["Names", "Names.More", "Names$u0027.Types"], ignoreOrder: true);
        }
        var generated = CSharpFiles(generation);
        var expected = Path.Combine(fixture, "csharp");
        if (Environment.GetEnvironmentVariable("EQDAML_UPDATE_BINDING_FIXTURES") == "1")
        {
            if (Directory.Exists(expected))
            {
                Directory.Delete(expected, recursive: true);
            }
            foreach (var file in generated)
            {
                var target = Path.Combine(expected, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, file.Value);
            }
            return;
        }
        var checkedIn = CSharpFiles(expected);
        generated.Keys.ShouldBe(checkedIn.Keys, ignoreOrder: true, "the generator writes other files than the fixture has; regenerate it");
        foreach (var file in generated)
        {
            file.Value.ShouldBe(checkedIn[file.Key], $"{file.Key} differs from what the generator writes; regenerate the fixture");
        }
    }

    // Relative path → content with LF line ends, as git keeps the fixture whatever the checkout does.
    private static Dictionary<string, string> CSharpFiles(string folder) =>
        Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
            .ToDictionary(f => f.Substring(folder.Length + 1), f => File.ReadAllText(f).Replace("\r\n", "\n"), StringComparer.OrdinalIgnoreCase);
}
