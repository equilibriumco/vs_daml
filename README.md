# EQ Daml Tools for Visual Studio

Visual Studio 2026 extension for [Daml](https://docs.digitalasset.com/), plus MSBuild packages that
build Daml packages and generate C# from them through the Canton .NET SDK (`dpm codegen-cs`).

Status: **0.9.0 beta**. Windows x64, Visual Studio 2026 only.

## Features

- **Editor**: errors, hover, completion and Go To Definition from the `damlc` language server;
  native coloring, folding, smart indent, snippets and brace handling.
- **Script results**: run a Daml Script from the editor and view the contract table or transaction
  view next to the code.
- **Open Folder**: build, test and clean `daml.yaml` and `multi-package.yaml` workspaces; errors in
  the Error List.
- **MSBuild** (`Equilibrium.Daml.Sdk`): Daml packages as `.msbuildproj` projects with incremental
  `dpm build`.
- **C# generation** (`Equilibrium.Daml.Build.CSharp`): C# bindings generated in the same build,
  navigation from C# to Daml, and C# reference counts in Daml files.
- **Templates** (`Equilibrium.Daml.Templates`): Visual Studio and `dotnet new` templates for Daml and
  Daml + .NET solutions.
- No telemetry.

## Requirements

| Component | Version |
|---|---|
| Visual Studio | 2026 (18.0+), any edition, x64 |
| dpm | 1.0.22+ with Daml SDK 3.6.x or 3.5.x |
| JDK | 17+ (C# generation, scripts and tests) |
| .NET SDK | 10.0 (solutions with C# projects) |

## Install

- Extension: Visual Studio Marketplace, *EQ Daml Tools for Visual Studio*.
- Packages: `Equilibrium.Daml.Sdk`, `Equilibrium.Daml.Build.CSharp` and `Equilibrium.Daml.Templates` on
  nuget.org (prerelease).

See the [user guide](docs/user-guide.md) for setup, settings, build properties, diagnostic codes,
troubleshooting and known limitations. Testing the beta: [a short tour and how to report
problems](docs/beta-testing.md).

## Support

Report issues on [GitHub Issues](https://github.com/equilibriumco/vs_daml/issues) with a diagnostic
report from Extensions > EQ Daml Tools > Export Diagnostics.

## Building from source

Requires Visual Studio 2026 (or Build Tools 2026 with the .NET workload), .NET SDK 10, PowerShell 7,
dpm and a JDK.

```powershell
pwsh scripts/bootstrap.ps1                  # check the toolchain; -Install adds dpm and the pinned SDKs
pwsh scripts/build.ps1                      # build
pwsh scripts/test.ps1 -Category Unit        # Unit, Process, Editor, Contract, MSBuild, Integration
pwsh scripts/pack.ps1                       # VSIX, NuGet packages and SBOMs in artifacts/
pwsh scripts/install-exp.ps1                # install the VSIX in the experimental instance
pwsh scripts/run-exp.ps1 -Path samples\single-package
```

`Contract`, `MSBuild -Backend Real` and `Integration` tests need dpm and a JDK; `Integration` also
needs an unlocked interactive session.

**Releasing.** The *Release* workflow runs by hand with a build number. From a branch it builds,
runs the `Hosted` test gate and keeps the files as an artifact. From a tag `v<version>` (for example
`v0.9.0-beta.3` with build number 3) it also drafts a GitHub pre-release with the VSIX, the packages
and their SBOMs. A hosted runner can't run the `Contract`, `Integration` or real-backend `MSBuild`
tests, so before publishing, run the full gate on the tagged commit on a machine with Visual Studio
2026, dpm and a JDK: `pwsh scripts/test.ps1 -Category All -Gate Full -RequiredTools`. Then publish the
workflow's exact files by hand: the VSIX on the Visual Studio Marketplace (listing text in
`src/Equilibrium.Daml.VisualStudio/Marketplace/overview.md`), the packages on nuget.org, then the draft.

| Folder | Contents |
|---|---|
| `src/` | Extension (`Equilibrium.Daml.VisualStudio`), host-independent libraries, MSBuild tasks and packages |
| `test/` | Tests, fakes and fixtures |
| `samples/` | Sample workspaces |
| `grammar/`, `native/` | Tree-sitter grammar for Daml (separate artifact) |
| `third_party/` | Pinned upstream assets with their licenses |

## License

Apache License 2.0, see [LICENSE](LICENSE). Third-party components are listed in [NOTICE](NOTICE).

## Trademarks

EQ Daml Tools for Visual Studio is an independent project by Equilibrium and is not affiliated with,
sponsored by, or endorsed by Digital Asset. Daml and Canton are trademarks of Digital Asset
(Switzerland) GmbH. Visual Studio is a trademark of Microsoft Corporation.
