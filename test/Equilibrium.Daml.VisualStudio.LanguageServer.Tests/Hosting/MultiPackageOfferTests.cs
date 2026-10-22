using System;
using System.IO;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Hosting;

// The multi-package.yaml offer as the user meets it: once a session, never again after "don't ask",
// and the file it writes never replaces one that is there.
public sealed class MultiPackageOfferTests : IDisposable
{
    private readonly string _solution = Path.Combine(Path.GetTempPath(), "daml-offer-" + Guid.NewGuid().ToString("N"));
    private readonly MultiPackageOffer _offer;
    private readonly string[] _manifests;

    public MultiPackageOfferTests()
    {
        var files = new PhysicalFileSystem();
        _offer = new MultiPackageOffer(files, new YamlManifestReader(files));
        _manifests = [Package("Contracts"), Package("Contracts.Tests")];
    }

    public void Dispose()
    {
        if (Directory.Exists(_solution))
        {
            Directory.Delete(_solution, recursive: true);
        }
    }

    private string Package(string name)
    {
        var manifest = Path.Combine(_solution, name, "daml.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, $"sdk-version: 3.6.0\nname: {name.ToLowerInvariant()}\nversion: 1.0.0\nsource: daml\n");
        return manifest;
    }

    private string MultiPackage => Path.Combine(_solution, "multi-package.yaml");

    [Fact, Trait("Category", "Unit")]
    public void The_offer_is_made_once_a_session()
    {
        _offer.ToShow(_solution, _manifests).ShouldNotBeNull().Kind.ShouldBe(MultiPackageAdvice.Create);
        _offer.ToShow(_solution, _manifests).ShouldBeNull("a restart shouldn't bring the bar back");
    }

    // A new project makes it a different offer, worth making again.
    [Fact, Trait("Category", "Unit")]
    public void Another_set_of_packages_is_offered_again()
    {
        _offer.ToShow(_solution, _manifests).ShouldNotBeNull();

        _offer.ToShow(_solution, [.. _manifests, Package("Lib")]).ShouldNotBeNull().Packages.Count.ShouldBe(3);
    }

    [Fact, Trait("Category", "Unit")]
    public void Dont_ask_again_holds_for_the_solution_from_then_on()
    {
        _offer.Dismiss(_solution);

        File.Exists(Path.Combine(_solution, ".vs", "EquilibriumDamlTools", MultiPackageOffer.DismissalFileName)).ShouldBeTrue();
        new MultiPackageOffer(new PhysicalFileSystem(), new YamlManifestReader(new PhysicalFileSystem())).ToShow(_solution, _manifests).ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Creating_writes_the_offered_file_and_ends_the_offer()
    {
        var proposal = _offer.ToShow(_solution, _manifests).ShouldNotBeNull();

        _offer.Create(_solution, proposal).ShouldBeTrue();

        File.ReadAllText(MultiPackage).ShouldBe(MultiPackageAdvisor.Content(_solution, proposal.Packages));
        new MultiPackageAdvisor(new PhysicalFileSystem(), new YamlManifestReader(new PhysicalFileSystem())).Advise(_solution, _manifests).Kind.ShouldBe(MultiPackageAdvice.None);
    }

    // The user (or another tool) wrote one while the bar was up: theirs stays.
    [Fact, Trait("Category", "Unit")]
    public void Creating_never_replaces_a_file_that_appeared_meanwhile()
    {
        var proposal = _offer.ToShow(_solution, _manifests).ShouldNotBeNull();
        File.WriteAllText(MultiPackage, "packages:\n- ./Contracts\n");

        _offer.Create(_solution, proposal).ShouldBeFalse();

        File.ReadAllText(MultiPackage).ShouldBe("packages:\n- ./Contracts\n");
    }
}
