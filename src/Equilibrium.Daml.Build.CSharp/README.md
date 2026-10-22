# Equilibrium.Daml.Build.CSharp

Generates C# from Daml in the same build. A C# project references Daml projects of the solution or DAR
files; each build runs the pinned `dpm-codegen-cs` when a DAR changed and compiles the result with the
project.

```xml
<ItemGroup>
  <PackageReference Include="Equilibrium.Daml.Build.CSharp" Version="<version>" PrivateAssets="all" />
  <PackageReference Include="Daml.Runtime" Version="0.6.0" />
  <PackageReference Include="Daml.Ledger.Abstractions" Version="0.6.0" />
  <EqDamlReference Include="..\Contracts\Contracts.msbuildproj" Namespace="Contracts" />
  <EqDamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />
</ItemGroup>
```

- `EqDamlReference` points at a Daml project (`Equilibrium.Daml.Sdk`), which builds first. `EqDamlDar`
  points at a DAR outside the solution. `Namespace` is optional.
- The generator is downloaded once per machine (about 180 MB) into `%LOCALAPPDATA%\EquilibriumDamlTools`;
  allow it with `dotnet build -p:EqDamlAllowToolInstall=true`.
- Generated code goes to `obj` by default, or to `Generated\Daml` with `EqDamlGeneratedOutputMode=Source`.
  A failed or canceled generation keeps the previous code.

Requires dpm 1.0.22 or later, JDK 17 or later, the .NET 10 SDK, and a project targeting `net10.0`.

Build properties, diagnostic codes and troubleshooting:
[user guide](https://github.com/equilibriumco/vs_daml/blob/main/docs/user-guide.md).

Independent Daml tooling by Equilibrium — not affiliated with or endorsed by Digital Asset. Daml is a
trademark of Digital Asset (Switzerland) GmbH.
