using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.MSBuild;

// The checked-in generator output the binding index is tested against, held to what the pinned
// generator writes today. With DAML_UPDATE_BINDING_FIXTURES=1 it is rewritten instead.
public sealed class BindingFixtureTests : IDisposable
{
    private static readonly string Fixture = Path.Combine(Repository.Root, @"test\fixtures\bindings\kinds");

    private readonly MSBuildSandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    public void The_kinds_fixture_is_what_the_pinned_generator_writes()
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var java = RequiredTools.Require(ExternalTool.Java);
        var dotnet = RequiredTools.Require(ExternalTool.Dotnet);
        _sandbox.UseSdkFeed();
        _sandbox.Write(@"contracts\daml.yaml", File.ReadAllText(Path.Combine(Fixture, "daml.yaml")));
        foreach (var file in Directory.GetFiles(Path.Combine(Fixture, "daml"), "*.daml", SearchOption.AllDirectories))
        {
            _sandbox.Write(Path.Combine("contracts", file.Substring(Fixture.Length + 1)), File.ReadAllText(file));
        }
        _sandbox.Write(@"contracts\Contracts.msbuildproj", $"<Project Sdk=\"Daml.Sdk/{SdkFeed.Version}\" />\n");
        _sandbox.Write(@"App\App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Daml.Build.CSharp" Version="{SdkFeed.Version}" PrivateAssets="all" />
                <PackageReference Include="Daml.Runtime" Version="{SdkFeed.RuntimeVersion}" />
                <PackageReference Include="Daml.Ledger.Abstractions" Version="{SdkFeed.RuntimeVersion}" />
                <DamlReference Include="..\contracts\Contracts.msbuildproj" Namespace="Fixture" />
              </ItemGroup>
            </Project>
            """);
        var properties = new Dictionary<string, string>
        {
            ["DamlDpmPath"] = dpm,
            ["DamlJavaHome"] = Path.GetDirectoryName(Path.GetDirectoryName(java))!,
            // Installs the generator once on a machine that hasn't got it yet.
            ["DamlAllowToolInstall"] = "true",
        };

        var build = _sandbox.Build(MSBuildHost.Dotnet, dotnet, @"App\App.csproj", properties: properties);

        build.ExitCode.ShouldBe(0, build.Log);
        var generation = Directory.GetDirectories(Path.Combine(_sandbox.Root, @"App\obj\Debug\net10.0\Daml")).ShouldHaveSingleItem();
        var generated = CSharpFiles(generation);
        var expected = Path.Combine(Fixture, "csharp");
        if (Environment.GetEnvironmentVariable("DAML_UPDATE_BINDING_FIXTURES") == "1")
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
