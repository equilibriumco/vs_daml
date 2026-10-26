# Contributing

## EQ Daml Tools for Visual Studio

To contribute to EQ Daml Tools for Visual Studio, start with a development copy of the repository.
You may want to use the GitHub interface to fork [vs_daml](https://github.com/equilibriumco/vs_daml) and check out your fork.
For development environment setup and the first build, see [Building from source](README.md#building-from-source).

### Legal Notice

When contributing to this project, you must agree that you have authored 100% of the content, have the necessary rights, and that the content you contribute may be provided under the project license ([Apache License 2.0](LICENSE)).

Code taken from other projects goes only into `third_party/<name>/`, with its license file and an entry in [NOTICE](NOTICE). Product and package names follow [TRADEMARK.md](TRADEMARK.md).

## Versioning

The project uses [SemVer](https://semver.org/) for versioning.
For the versions available, see the [tags on this repository](https://github.com/equilibriumco/vs_daml/tags): `v<version>` for the extension and the NuGet packages, `tree-sitter-daml-v<version>` for the grammar.
Maintainers set the version when they prepare a release; pull requests don't change it.

## Developer Workflow

Changes to the project are proposed through pull requests. The general pull request workflow is as follows:
1. Fork this project on GitHub and check out your copy of the repository to start contributing.
2. Ensure your fork is up-to-date and create a topic branch for your feature or bug fix.
3. Do your changes. Each commit and tag should be signed using your GPG key.
4. Make sure the build and the tests pass locally: `pwsh scripts/build.ps1` without warnings, and `pwsh scripts/test.ps1 -Category All -Gate PR -RequiredTools`, the same check CI runs. Changes to the language server client or the MSBuild packages should also pass the `Contract` and `MSBuild -Backend Real` tests, which need dpm and a JDK.
5. Add tests with the change: xUnit with Shouldly for .NET code, Pester for the scripts in `scripts/`, `node:test` for the grammar scripts.
6. Update [docs/user-guide.md](docs/user-guide.md) with user-visible changes, including new settings, build properties and diagnostic codes.
7. You may merge the Pull Request once you have at least one accepting review.

When creating a pull request, its description should reference the corresponding issue ID.

If you've been working on a change for a while, it is very likely that some other changes are already made in the repository. To solve this problem, [rebase](https://docs.github.com/en/get-started/using-git/about-git-rebase) with *upstream/main*.

```shell
git fetch upstream
git rebase upstream/main
git push origin my-feature-branch -f
```

Project history should be bisect-able so that when regressions are identified it could be easily to use git bisect to be able to pin-point the exact commit which introduced the regression. This requires that every commit is able to be built and passes all lints and tests. So if your pull request includes multiple commits be sure that each and every commit is able to be built and passes all checks performed by CI.

### A note on "big" changes

When planning "big" changes to the project, it's encouraged to ask for feedback on an issue before implementing things in code. The created issue should explain, at a minimum:

* The motivation for such a change.
* What would be the suggested implementation and what other alternatives were considered.

How big is "big"? As a rule of thumb, if the work spans more than one coding day, it's probably advisable to create an issue first and then work on it after confirmation. However, this may differ depending on the contributor context. I.e. if the author is already a maintainer, the base context will be high and the risk of rejection low. It is probably the opposite in the case of an external, non-habitual contributor. So, the author should use common sense here.

## Style Guides

### Commit Messages

Commits should be [atomic](https://en.wikipedia.org/wiki/Atomic_commit#Atomic_commit_convention) and broken down into logically separate changes. Diffs should also be made easy for reviewers to read and review, so formatting fixes or code moves should not be included in commits with actual code changes.

We are using the [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) convention for our commit messages. This convention dovetails with SemVer, describing the features, fixes, and breaking changes in commit messages.

### Code style

C# follows the [.NET coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions) and the repository's [.editorconfig](.editorconfig). The build treats warnings as errors, with nullable reference types and the .NET and Visual Studio threading analyzers on, and CI checks formatting with `dotnet format Equilibrium.Daml.VisualStudio.sln --verify-no-changes`.

A few rules specific to this project:

- Never block the Visual Studio UI thread: use `JoinableTaskFactory` and `async`/`await`.
- Keep logic that doesn't need Visual Studio in the `netstandard2.0` libraries (`*.Core`, `Equilibrium.Daml.Build.Core`), so it can be tested without the IDE; MEF parts, commands and windows stay thin.
- Start external tools (`dpm`, `damlc`, `java`) through `IProcessRunner`, not `Process.Start`.
- Add NuGet dependencies to `Directory.Packages.props` (Central Package Management) and explain them in the pull request. Don't raise the versions of assemblies that Visual Studio ships.
- Unit tests don't use the network; tests that need dpm or a JDK carry the `RequiresDaml` or `RequiresJava` trait.
- The extension sends no telemetry.

## I Have a Question

Before you ask a question, searching for existing [Issues](https://github.com/equilibriumco/vs_daml/issues) that might help you is best. In case you have found a suitable issue and still need clarification, you can write your question in this issue. It is also advisable to search the internet for answers first.

If you still feel the need to ask a question and need clarification, we recommend the following:

- Open an [Issue](https://github.com/equilibriumco/vs_daml/issues/new).
- Provide as much context as possible about what you're running into.
- Provide project and platform versions (Visual Studio, the extension, dpm and the Daml SDK, .NET SDK), depending on what seems relevant. A report from Extensions > EQ Daml Tools > Export Diagnostics includes most of them.
