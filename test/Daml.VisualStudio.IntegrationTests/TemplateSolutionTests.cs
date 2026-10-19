using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

public class TemplateSolutionTests
{
    // The first build runs dpm and the C# generator, which can be slow on a cold machine.
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(6);

    // The app template's solution, as File > New > Project makes it, built right after it opens, as
    // after a clone: the Daml package is built, its C# generated and compiled into the app, and the
    // app runs. With background sync off, Visual Studio's up-to-date check then skips the Daml
    // projects; the C# project builds once more, as the first build restored its packages, and finds
    // its Daml package current without dpm; after that nothing runs at all. A changed Daml source
    // makes it build the package again.
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 20 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    public async Task A_new_app_solution_builds_runs_and_is_then_up_to_date()
    {
        var folder = await Ide.NewSolutionFromTemplateAsync("daml-dotnet-app", "Shop");
        await Ide.OpenSolutionAsync(Path.Combine(folder, "Shop.slnx"));
        try
        {
            var projects = Ide.SolutionProjects();
            Assert.Equal(new[] { "Shop", "Shop.Contracts", "Shop.Contracts.Tests" }, projects.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.All(projects, p => Assert.True(p.Loaded, p.Name + " didn't load."));

            Assert.True(await Ide.BuildSolutionAsync(BuildTimeout) == 0, "The first build failed." + Environment.NewLine + Ide.BuildReport());
            var app = Path.Combine(folder, @"Shop\bin\Debug\net10.0\Shop.exe");
            Assert.Equal("Gold from Daml package shop-contracts 0.1.0", (await Ide.RunAsync(folder, app)).Trim());

            // What only a run of the Daml project's build touches, what only dpm writes, and what only
            // the generator writes.
            var buildRecord = Path.Combine(folder, @"Shop.Contracts\obj\daml.build.state");
            var dar = Path.Combine(folder, @"Shop.Contracts\.daml\dist\shop-contracts-0.1.0.dar");
            var manifests = Directory.GetFiles(Path.Combine(folder, @"Shop\obj\Debug\net10.0\Daml"), "*.manifest", SearchOption.AllDirectories);
            Assert.NotEmpty(manifests);
            var afterFirst = Stamps(buildRecord, dar, app, manifests);

            Assert.True(await Ide.BuildSolutionAsync(BuildTimeout) == 0, "The second build failed." + Environment.NewLine + Ide.BuildReport());
            var before = Stamps(buildRecord, dar, app, manifests);
            // The C# project's build checks its Daml package, which only refreshes the build record.
            Assert.True(before[1] == afterFirst[1], "The second build built the Daml package again." + Environment.NewLine + Ide.BuildReport());
            Assert.True(await Ide.BuildSolutionAsync(BuildTimeout) == 0, "The third build failed." + Environment.NewLine + Ide.BuildReport());
            var afterThird = Stamps(buildRecord, dar, app, manifests);
            Assert.True(afterThird.SequenceEqual(before), "Build Solution ran the build again: " + string.Join(", ", before) + " / " + string.Join(", ", afterThird) + Environment.NewLine + Ide.BuildReport());

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            File.AppendAllText(Path.Combine(folder, @"Shop.Contracts\daml\Main.daml"), Environment.NewLine + "-- edited" + Environment.NewLine);
            Assert.True(await Ide.BuildSolutionAsync(BuildTimeout) == 0, "The build after the edit failed." + Environment.NewLine + Ide.BuildReport());
            Assert.True(File.GetLastWriteTimeUtc(dar) > before[1], "The edited package wasn't built again." + Environment.NewLine + Ide.BuildReport());
            Assert.Equal("Gold from Daml package shop-contracts 0.1.0", (await Ide.RunAsync(folder, app)).Trim());
        }
        finally
        {
            await Ide.CloseSolutionAsync();
        }
    }

    private static DateTime[] Stamps(string buildRecord, string dar, string app, string[] manifests) =>
        new[] { buildRecord, dar, app }.Concat(manifests).Select(File.GetLastWriteTimeUtc).ToArray();
}
