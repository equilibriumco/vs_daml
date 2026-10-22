# Daml for Visual Studio: user guide

Version 0.9.0 beta. Daml editing in Visual Studio 2026 backed by the `damlc` language server, native
syntax features, Daml Script results next to the code, Daml packages as MSBuild projects, and C#
generated from Daml in the same build.

## Requirements

| Component | Version |
|---|---|
| Visual Studio | 2026 (18.0 or later), any edition, x64. Tested on 18.12. Visual Studio 2022 is not supported. |
| dpm | 1.0.20 or later for editing; 1.0.22 or later for building Daml projects and generating C#. Found on `PATH`, in `%DPM_HOME%\bin`, `%APPDATA%\.dpm\bin` or `%APPDATA%\dpm\bin`. |
| Daml SDK | 3.6.x or 3.5.x (`dpm install <version>`). Packages on SDKs older than 3.4 use the `daml` assistant if it is installed. |
| JDK | 17 or later, for C# generation and for running scripts and tests. Not needed for editing. `JAVA_HOME` or `java` on `PATH`. |
| .NET SDK | 10.0, for solutions with Daml and C# projects. Generated C# targets `net10.0` and needs `Daml.Runtime` and `Daml.Ledger.Abstractions` 0.6.0. |

MSBuild builds are tested with MSBuild 18.11 and 18.12 and with `dotnet build` (.NET SDK 10.0.402).

## Installation

**Extension.** Extensions > Manage Extensions, search for *Daml for Visual Studio*, install, restart
Visual Studio. Or close Visual Studio and run `Daml.VisualStudio.vsix`. A newer version installs over
the old one and keeps your settings. Uninstall from Extensions > Manage Extensions > Installed.

**NuGet packages.** `Daml.Sdk`, `Daml.Build.CSharp` and `Daml.Templates` are on nuget.org as
prerelease versions. Install the project templates with:

```
dotnet new install Daml.Templates::0.9.0-beta.1
```

NuGet caches a version once in `%USERPROFILE%\.nuget\packages`. If you replace a package with a
different build of the same version, delete its folder there with Visual Studio closed.

## Editor

Open a folder that contains `daml.yaml` or `multi-package.yaml` (File > Open > Folder), or a solution
with Daml projects, then open a `.daml` file. The status bar shows *Daml: starting…* and then
*Daml: ready*.

- **Language server** (`damlc`): errors and warnings as you type, hover, completion, Go To Definition
  and Peek, including other packages of a `multi-package.yaml` workspace and library sources. Library
  sources open as read-only copies from `%LOCALAPPDATA%\DamlVisualStudio\library`.
- **Colors** come from the extension's own lexer. Entries are under Tools > Options > Environment >
  Fonts and Colors, named `Daml - …`, and follow the light, dark, blue and high-contrast themes.
- **Folding**: templates, interfaces, choices, data types, classes, instances, top-level functions,
  imports, `where`/`let`/`case` blocks, `do`/`with` blocks of three lines or more, multi-line comments.
- **Smart indent** after `where`, `with`, `do`, `of`, `let`, `=`, `->`, `then`, `else`, `$` and open
  brackets. Requires indenting mode *Smart* (Tools > Options > Text Editor > Tabs).
- **Snippets** at the start of a line: `template`, `interface`, `script`, `module` at the top level;
  `choice`, `nonconsuming choice`, `interface instance` inside a template or interface.
- **Editing**: `--` comments (Ctrl+K,C / Ctrl+K,U); `(`, `[`, `{` and `"` close automatically;
  brace matching and highlighting of the name under the caret (by spelling, not by scope).
- **Script results**: a *▶ Script results* row appears between a script's signature and its
  definition. Click it, press Ctrl+. on the line, or use Extensions > Daml > Show Script Results at
  Caret. The window shows the contract table or the transaction view, follows the theme, works from
  the keyboard, and keeps the chosen view per script. It needs the Microsoft Edge WebView2 Runtime,
  which ships with Windows 11.
- **Open Folder build**: right-click `daml.yaml` > Build, Test or Clean; `multi-package.yaml` >
  Build All or Clean All. Output goes to the *Daml Build* pane, errors to the Error List.
- **Extensions > Daml**: Restart Language Server, Show Language Server Log, Export Diagnostics,
  Build Current Package, Test Current Package, Show Script Results at Caret, Open Daml Documentation.

**Solutions.** The server covers all Daml projects of the solution, including ones outside its
folder, and restarts when one is added or removed. Go To Definition across packages needs a
`multi-package.yaml` that lists them; when it is missing or incomplete, a bar offers to create it or
names the missing packages. The extension never edits an existing file.

**Not available.** `damlc` 3.5 and 3.6 do not implement Find All References, Rename, Format
Document, signature help or workspace symbols, so Visual Studio does not offer them in Daml files.

### Keyboard

| Keys | Action |
|---|---|
| Ctrl+. on a line with a lens row | The row's actions: show script results, show C# references |
| Ctrl+M, Ctrl+O / Ctrl+M, Ctrl+L | Collapse to definitions / toggle all folds |
| Ctrl+Space | Completion, including snippets at the start of a line |
| F12 / Alt+F12 | Go To Definition / Peek |
| Ctrl+K, C / Ctrl+K, U | Comment / uncomment |
| (unbound) | *Go to Daml Definition* in C#; bind `GoToDamlDefinition` under Tools > Options > Environment > Keyboard |

## Daml projects in MSBuild (`Daml.Sdk`)

A Daml package becomes a solution project with a project file next to its `daml.yaml`:

```xml
<Project Sdk="Daml.Sdk/0.9.0-beta.1">
</Project>
```

The SDK version can also go in `global.json` under `msbuild-sdks`. The templates *Daml Contracts
Project* and *Daml and .NET App* (`dotnet new daml-contracts`, `dotnet new daml-dotnet-app`) create
these projects and a `.slnx`. In Visual Studio's New Project dialog, check *Place solution and project
in the same directory* to get the same layout as `dotnet new`.

- **Build** runs `dpm build` only when an input changed: `daml.yaml`, the set and content of source
  files, dependency DARs/DALFs, or the effective DPM configuration. Errors use codes `EQD0001`
  (error) and `EQD0002` (warning).
- **Clean** deletes the DAR and its build record.
- **Test**: `dotnet build -t:DamlTest` runs every script with `dpm test`.
- **Order**: a project that needs another package's DAR references it with
  `<ProjectReference … ReferenceOutputAssembly="false" />`.
- Concurrent builds of one package run `dpm` one at a time; a build waiting longer than
  `DamlLockTimeoutSeconds` fails with `EQD0105`.
- Works in Visual Studio, `MSBuild.exe` of Visual Studio 2026 and `dotnet build`.

## C# from Daml (`Daml.Build.CSharp`)

```xml
<ItemGroup>
  <PackageReference Include="Daml.Build.CSharp" Version="0.9.0-beta.1" PrivateAssets="all" />
  <PackageReference Include="Daml.Runtime" Version="0.6.0" />
  <PackageReference Include="Daml.Ledger.Abstractions" Version="0.6.0" />
  <DamlReference Include="..\Contracts\Contracts.msbuildproj" Namespace="Contracts" />
  <DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />
</ItemGroup>
```

- `DamlReference` points at a Daml project in the solution, which builds first. A plain
  `ProjectReference` works too, with the namespace in `DamlNamespace` metadata. `DamlDar` points at a
  DAR outside the solution.
- `Namespace` is optional and prefixes the generated namespaces. Two references that generate the
  same types fail with `EQD0202`.
- C# is generated for the DAR's own package only. Dependencies need their own references or NuGet
  packages.
- **Generator**: `dpm-codegen-cs` 0.6.0, pinned by digest and cached in
  `%LOCALAPPDATA%\DamlVisualStudio\codegen`. A build downloads it (about 180 MB) only when
  `DamlAllowToolInstall` is true; otherwise a missing generator fails with `EQD0101`. Run
  `dotnet build -p:DamlAllowToolInstall=true` once per machine. Apps created from the template allow
  the download by default.
- **Output**: `Intermediate` (default) writes to `obj`; `Source` writes to `Generated\Daml` for
  check-in and supports a single target framework only.
- Unchanged inputs skip the generator. A new generation replaces the old one only when it is
  complete; a failed or canceled run keeps the previous code, and the next build finishes or undoes
  an interrupted one. Clean removes only files the generator wrote; your own files
  in that folder are kept and reported (`EQD0108`).
- **In Visual Studio**, IntelliSense picks up new types after each build without a project reload.
  Design-time builds never run tools.
- **Navigation**: in C#, *Go to Daml Definition* (context menu) opens the Daml declaration of a
  generated type or member. In Daml, a *C#: N references* row above each template, interface and
  choice counts its uses in C# and opens a list. Both report when the Daml changed since the last
  build.

### Build properties

| Property | Applies to | Default | Description |
|---|---|---|---|
| `DamlDpmPath` | both | empty | dpm to run. Empty: `PATH`, `%DPM_HOME%\bin`, `%APPDATA%\.dpm\bin`, `%APPDATA%\dpm\bin`. |
| `DamlOffline` | both | false | Never download or install; missing tools fail with `EQD0107`. Overrides `DamlAllowToolInstall`. |
| `DamlAllowToolInstall` | both | false | Allow installing a missing SDK or downloading the generator. |
| `DamlSkipPreflight` | both | false | Skip dpm/JDK version checks (with a warning). |
| `DamlProcessTimeoutSeconds` | both | 1800 | Time limit for one dpm run; 0 disables it (`EQD0110`). |
| `DamlLockTimeoutSeconds` | both | 600 | Wait limit for another build of the same package or generation (`EQD0105`). |
| `DamlManifestPath` | Daml | `daml.yaml` next to the project | Package manifest. |
| `DamlBuildEnabled` | Daml | true | false skips `dpm build` and uses the existing DAR. |
| `DamlCodegenEnabled` | C# | true | false disables generation. |
| `DamlCodegenComponent` | C# | pinned generator | Another generator as `oci://<registry>/<repo>:<tag>@sha256:<digest>`; untested ones raise `EQD0106`. |
| `DamlGeneratedOutputMode` | C# | `Intermediate` | `Intermediate` or `Source`. |
| `DamlGeneratedDir` | C# | `obj\…\Daml\` or `Generated\Daml\` | Output folder; must be a folder of its own. |
| `DamlJavaHome` | C# | `$(JAVA_HOME)` | JDK for the generator. |

Boolean values accept MSBuild forms (`true`/`false`, `on`/`off`, `yes`/`no`). Other values raise
`EQD0100`.

### Diagnostic codes

| Code | Meaning | Fix |
|---|---|---|
| `EQD0001` / `EQD0002` | Daml compiler error / warning. | Fix the Daml at the reported location. |
| `EQD0100` | Invalid property, item, path or `daml.yaml` field. | See the message. |
| `EQD0101` | dpm, the pinned SDK or the generator is not installed. | Install it (`dpm install 3.6.0`), set `DamlDpmPath`, or set `DamlAllowToolInstall`. |
| `EQD0102` | No JDK 17+ for the generator. | Install a JDK or set `DamlJavaHome`. |
| `EQD0103` | dpm older than 1.0.22. | Update dpm. |
| `EQD0104` | Generation failed. | See the message; for a locked file, build again. |
| `EQD0105` | Lock wait exceeded `DamlLockTimeoutSeconds`. | Wait, or raise the limit. |
| `EQD0106` | Generator not pinned, not tested, or its cache is damaged. | Use the default generator; delete its folder under `codegen`. |
| `EQD0107` | Offline build needs a missing tool. | Build once online with `DamlAllowToolInstall`. |
| `EQD0108` | Generated code would overwrite or delete files it does not own. | See the files named in the message. |
| `EQD0109` | A failed generation could not restore the previous code. | Build again. |
| `EQD0110` | dpm or the generator was canceled or timed out. | Build again, or raise the limit. |
| `EQD0111` | Warning: generated path exceeds 260 characters. | Shorten `DamlGeneratedDir` or enable Windows long paths. |
| `EQD0201` | Runtime packages missing or incompatible, or target framework below `net10.0`. | Reference both runtime packages at 0.6.0; target `net10.0`. |
| `EQD0202` | Two references generate the same types. | Give one a `Namespace`. |
| `EQD0203` | C#/Daml navigation not possible: sources ambiguous or changed since the build. | Build. |

## Settings

Tools > Options > Daml > General.

| Setting | Default | Description |
|---|---|---|
| dpm path | empty | Path to `dpm.exe`/`dpm.cmd`. |
| Daml assistant path | empty | Path to `daml`, for SDKs older than 3.4. |
| Use multi-package server | on | One `damlc multi-ide` server for the workspace. Off: `damlc ide` for the active file's package. |
| Log level | Info | Server log detail in the *Daml Language Server* pane. |
| Extra arguments | empty | Additional `damlc` arguments. |
| Run all scripts on open | off | Passes `--studio-auto-run-all-scripts=yes`. |
| Restart an unresponsive server | on | Restarts the server if it does not answer within two minutes. |
| Update C# bindings in the background (preview) | off | Regenerates C# about two seconds after a Daml file is saved, so IntelliSense sees new types before the next build. |

Changes to the first six settings apply after a server restart; a bar offers **Restart now**.

## Privacy and network

The extension sends no telemetry and no data. dpm uses the network to list remote SDKs, install
SDKs and download components such as the C# generator (ghcr.io). `dpm build`, `dpm test` and the
language server opened no remote TCP connections in our measurements. `damlc ide` accepts hidden
`--telemetry` switches; the extension never passes them.

## Files on your machine

Settings live in Visual Studio's settings store and survive updates and uninstall. Extension data is
in `%LOCALAPPDATA%\DamlVisualStudio`, shared by all Visual Studio instances and command-line builds:

| Folder | Contents | Safe to delete |
|---|---|---|
| `codegen` | Pinned C# generator | Yes; downloaded again on the next generating build |
| `compiler-cache` | `damlc` caches | When no build is running |
| `locks` | Build lock files | When no build is running |
| `library` | Library source copies for Go To Definition | Yes |
| `WebView2`, `webview` | Script results browser profile and pages | With Visual Studio closed |
| `logs` | Extension logs | Yes |
| `diagnostics` | Exported diagnostic reports | Yes |

Uninstalling does not remove this folder. Delete it manually after uninstalling, together with
`%LOCALAPPDATA%\Daml\VisualStudio` if a pre-0.9 version created it. The extension never deletes dpm,
installed SDKs, project build output or the NuGet cache.

## Troubleshooting

**Logs.** View > Output, panes *Daml Language Server* (server start, command line, server log),
*Daml Build* and *Daml*. The same entries go to `%LOCALAPPDATA%\DamlVisualStudio\logs`, one file per
session, rotated at 10 MB, five files kept, files older than seven days deleted. Logs can contain file
paths and compiler messages that quote your code.

**Diagnostic report.** Extensions > Daml > Export Diagnostics writes a report with versions,
settings and recent logs to `%LOCALAPPDATA%\DamlVisualStudio\diagnostics` and opens it. Paths, user
and machine names, quoted code, script results and credentials are masked. Review it before you
share it. Visual Studio's own protocol log in `%TEMP%\VisualStudio\LSP` is not included and contains
full file contents.

| Symptom | Cause and fix |
|---|---|
| Bar: "No daml.yaml found …" | The folder has no `daml.yaml` or `multi-package.yaml` above the file. Open the package or workspace folder. |
| Bar with `EQD0101` | dpm or the pinned SDK is missing. Install it or set the dpm path in Options. |
| Bar: "… keeps crashing and was stopped." | More than five crashes in three minutes. Check the *Daml Language Server* pane, fix the cause, click **Restart**. |
| Bar: "… stopped responding and was restarted." | No answer for two minutes. Report it if it repeats. |
| Colors but no errors or hover | The file is outside the open workspace. |
| CS0246 on generated types although the build succeeds | Visual Studio evaluated the project before the first generation. The extension re-evaluates after the next build; if it persists, reload the project. |
| Errors listed twice | Live errors from the server and build errors from the last build. Build again or edit the file. |

## Known limitations

- Visual Studio 2026 on x64 only; no ARM64. IDE features are tested on Visual Studio 2026 18.12.
- After you delete a generated file in `obj` by hand, the next Rebuild restores it, but IntelliSense
  may need a second Rebuild.
- Generated paths longer than 260 characters (deep `Namespace` values) build with warning `EQD0111`,
  but IntelliSense reports missing types unless Windows long paths are enabled.
- The first generating build on a machine downloads the generator (about 180 MB) and needs
  `DamlAllowToolInstall` unless the project came from the app template.
- C# is generated for a DAR's own package only; it requires `net10.0` and the 0.6.0 runtime packages.
- `Source` output mode supports one target framework.
- Background C# binding updates are a preview and off by default; removed types may stay in
  IntelliSense until the next build or project reload.
- Go To Definition on a module name in an `import` line finds nothing (`damlc`).
- With *Use multi-package server* off, the server serves the package that was active at start;
  restart it after switching packages.
- In a multi-package workspace, every package opened during a session keeps its sub-server until the
  language server restarts.
- Script results rows require a running server; results windows are not restored with the window
  layout.
- Daml item templates (Add > New Item) are available in Daml projects, not in Open Folder.
- Network measurements cover TCP connections of builds, tests and the language server. DNS, UDP and
  connections shorter than 50 ms were not measured.

## Support

Report problems in [GitHub Issues](https://github.com/equilibriumco/vs_daml/issues). Include the
Visual Studio version, the Daml SDK version and a diagnostic report (Extensions > Daml > Export
Diagnostics), reviewed before you attach it.
