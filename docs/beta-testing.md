# Testing the EQ Daml Tools beta

Thank you for trying EQ Daml Tools for Visual Studio 0.9 beta. This page takes you through the main
features in about 45 minutes and says what to send us when something goes wrong. The
[user guide](user-guide.md) describes everything in more detail.

A beta can have bugs we haven't found. Try it on your own Daml projects too, but keep a copy of
anything you can't afford to lose.

## Before you start

- Visual Studio 2026 (18.0 or later), any edition, on Windows x64.
- dpm 1.0.22 or later with a Daml SDK: `dpm install 3.6.0` (3.5.x works too).
- JDK 17 or later, for C# generation and for running scripts.
- .NET SDK 10.0.

## Install

1. **Extension.** In Visual Studio, Extensions > Manage Extensions, search for *EQ Daml Tools for
   Visual Studio* and install it, then restart Visual Studio. The VSIX is also attached to the
   release on [GitHub](https://github.com/equilibriumco/vs_daml/releases).
2. **Templates.** Install them with the version from the release page, for example:

   ```
   dotnet new install Equilibrium.Daml.Templates::0.9.0-beta.1
   ```

   The other two packages, `Equilibrium.Daml.Sdk` and `Equilibrium.Daml.Build.CSharp`, come in
   through the projects the templates create.

## A tour

### 1. Create a solution

Create a *Daml and .NET App*: in Visual Studio's New Project dialog (check *Place solution and project
in the same directory*), or with `dotnet new daml-dotnet-app -n MyApp`. Open the `.slnx`.

You get a Daml package with a template `Asset`, a Daml test package with the script `giveToBob`, and a
C# console app that uses `Asset`.

### 2. Build

Build the solution. The first build on a machine downloads the C# generator (about 180 MB), so it
takes a while. Expected: the build succeeds and the Error List is empty. Run the app; it prints
`Gold from Daml package …`.

### 3. Edit Daml

Open `daml\Main.daml` in the contracts project. The status bar shows *EQ Daml: ready* when the
language server is up.

- Hover over `Party`, and use Ctrl+Space for completion.
- In `Test.daml`, press F12 on `Asset`; it opens `Main.daml` in the other package.
- Misspell `owner` in the choice. An error appears as you type, in the editor and in the Error List.
  Undo it.
- Fold and unfold the template; type `choice` at the start of a line inside the template and pick the
  snippet; comment a line with Ctrl+K, C.
- Switch Visual Studio to another color theme and check that the Daml colors stay readable.

### 4. Script results

In `Test.daml`, click the *▶ Script results* row above `giveToBob`, or put the caret on its line and
press Ctrl+. Expected: a window with the contracts the script created, and a transaction view you can
switch to. The window needs the WebView2 Runtime, which comes with Windows 11.

### 5. Between C# and Daml

- In `Program.cs`, right-click `Asset` and choose *Go to Daml Definition*. It opens the template in
  `Main.daml`.
- In `Main.daml`, the row above `template Asset` says *C#: N references*. Click it to see where C#
  uses the template.
- Add a field to `Asset`, for example `amount : Int`, and build. Expected: the `new Asset(…)` call in
  `Program.cs` now fails to compile, and IntelliSense knows the new `Amount` property. Fix the call and
  build again.

### 6. Run the Daml tests

`dotnet build -t:EqDamlTest` in the test package's folder runs every script with `dpm test`.

### 7. Open Folder

Close the solution, choose File > Open > Folder and pick one of your own Daml packages or
workspaces, a folder with `daml.yaml` or `multi-package.yaml`. Right-click `daml.yaml` and choose
Build, then Test. Errors go to the Error List, output to the *EQ Daml Tools: Build* pane.

### 8. Your own projects

This is where we learn the most. Open your real Daml projects and work as you normally would. Tell us
when something is wrong, slow or confusing, or when a feature you expected is missing.

## Reporting a problem

Open an issue on [GitHub Issues](https://github.com/equilibriumco/vs_daml/issues) with:

- what you did, what you expected and what happened;
- the Visual Studio version (Help > About) and the Daml SDK version of the project;
- a diagnostic report: Extensions > EQ Daml Tools > Export Diagnostics. It has the extension, dpm
  and SDK versions, your settings and recent logs. Paths, user and machine names, quoted code and
  credentials are masked, but read it before you attach it.

Before reporting, check [Known limitations](user-guide.md#known-limitations); those are known
already.

## Updates and removal

New betas arrive through Extensions > Manage Extensions > Updates. For the packages, change the
version in your project files (`Equilibrium.Daml.Sdk/…` and the `Equilibrium.Daml.Build.CSharp`
reference) and install the new templates.

To remove the beta, uninstall the extension in Extensions > Manage Extensions > Installed and run
`dotnet new uninstall Equilibrium.Daml.Templates`. The extension leaves its data in
`%LOCALAPPDATA%\EquilibriumDamlTools`; delete it if you don't need it.
