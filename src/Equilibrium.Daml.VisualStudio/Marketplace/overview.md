# EQ Daml Tools for Visual Studio

Daml in Visual Studio 2026: editing backed by the `damlc` language server, Daml Script results next to
the code, Daml packages as MSBuild projects, and C# generated from Daml in the same build.

**Beta.** Windows x64, Visual Studio 2026 only. Please report problems on
[GitHub Issues](https://github.com/equilibriumco/vs_daml/issues).

## Features

- **Editor**: errors as you type, hover, completion and Go To Definition from `damlc`; coloring, folding,
  smart indent, snippets, brace completion; follows the light, dark, blue and high-contrast themes.
- **Script results**: a *▶ Script results* row above each Daml Script opens the contract table or the
  transaction view.
- **Open Folder**: build, test and clean `daml.yaml` and `multi-package.yaml` workspaces, with errors in
  the Error List.
- **Solutions**: Daml packages as projects (`Equilibrium.Daml.Sdk`), C# generated in the same build
  (`Equilibrium.Daml.Build.CSharp`), *Go to Daml Definition* from C#, and C# reference counts above Daml
  templates and choices.
- **Templates**: *Daml Contracts Project* and *Daml and .NET App* in New Project.
- No telemetry.

## Requirements

| Component | Version |
|---|---|
| Visual Studio | 2026 (18.0 or later), any edition, x64 |
| dpm | 1.0.22 or later, with Daml SDK 3.6.x or 3.5.x |
| JDK | 17 or later (C# generation, scripts and tests) |
| .NET SDK | 10.0 (solutions with C# projects) |

## Getting started

1. Install dpm and an SDK: `dpm install 3.6.0`.
2. Open a folder with a `daml.yaml` (File > Open > Folder) and open a `.daml` file. The status bar shows
   *EQ Daml: ready* when the language server is up.
3. For a solution, create a project from *Daml and .NET App*, then build.

The [user guide](https://github.com/equilibriumco/vs_daml/blob/main/docs/user-guide.md) covers settings,
build properties, diagnostic codes, troubleshooting and known limitations.

## Trademarks

EQ Daml Tools for Visual Studio is an independent project by Equilibrium and is not affiliated with,
sponsored by, or endorsed by Digital Asset. Daml and Canton are trademarks of Digital Asset
(Switzerland) GmbH. Visual Studio is a trademark of Microsoft Corporation.
