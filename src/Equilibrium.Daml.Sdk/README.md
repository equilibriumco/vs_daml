# Equilibrium.Daml.Sdk

MSBuild project SDK for Daml packages. A Daml package becomes a project in your solution: `dotnet build`,
`MSBuild.exe` and Visual Studio run `dpm build` when an input changed, report Daml errors at their
location, and hand the DAR to the projects that reference it.

```xml
<!-- Contracts.msbuildproj, next to daml.yaml -->
<Project Sdk="Equilibrium.Daml.Sdk/<version>">
</Project>
```

- **Build** runs `dpm build` only when `daml.yaml`, the sources, dependency DARs or the DPM configuration
  changed. Errors use codes `EQD0001` (error) and `EQD0002` (warning).
- **Test**: `dotnet build -t:EqDamlTest` runs every Daml Script with `dpm test`.
- **Clean** deletes the DAR and its build record.

Requires dpm 1.0.22 or later with Daml SDK 3.5.x or 3.6.x, and MSBuild 18 (Visual Studio 2026) or the
.NET 10 SDK. Generate C# from the DAR with `Equilibrium.Daml.Build.CSharp`; create projects with
`dotnet new install Equilibrium.Daml.Templates`.

Build properties, diagnostic codes and troubleshooting:
[user guide](https://github.com/equilibriumco/vs_daml/blob/main/docs/user-guide.md).

Independent Daml tooling by Equilibrium — not affiliated with or endorsed by Digital Asset. Daml is a
trademark of Digital Asset (Switzerland) GmbH.
