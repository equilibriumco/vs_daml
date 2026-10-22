# mixed-dotnet

A Daml + C# solution: Daml contracts and their Daml Script tests as MSBuild projects on `Equilibrium.Daml.Sdk`,
and a C# console app that uses the C# the build generates from the contracts through
`Equilibrium.Daml.Build.CSharp`.

| Path | What it is |
|---|---|
| `MixedDotnet.slnx` | the solution |
| `daml/Contracts.msbuildproj` | the contracts package (`daml/daml.yaml`), with the Splice token standard DARs in `daml/dars` as data-dependencies |
| `daml/test/Contracts.Tests.msbuildproj` | its Daml Script tests (`daml/test/daml.yaml`), built after the contracts |
| `App/App.csproj` | the C# app: `DamlReference` to the contracts, `Daml.Runtime`, `Daml.Ledger.Abstractions` and the `Splice.Api.Token.*` packages the generated code implements |
| `multi-package.yaml` | both Daml packages, for the language server and `dpm build --all` |

The `Directory.*` files are empty on purpose: they keep the repository's own build settings and
central package versions out, so the solution builds like a user's.

## Build

1. `pwsh scripts/pack.ps1` from the repository root leaves `Equilibrium.Daml.Sdk` and `Equilibrium.Daml.Build.CSharp`
   (`0.3.0-preview.0`) in `artifacts/packages`, where `nuget.config` here looks for them; everything
   else comes from nuget.org. NuGet keeps the first copy of a version it saw, so after a new pack
   delete `equilibrium.daml.sdk\0.3.0-preview.0` and `equilibrium.daml.build.csharp\0.3.0-preview.0` under
   `%USERPROFILE%\.nuget\packages`; `pack.ps1` warns and names the folders when they hold another copy.
2. dpm with SDK 3.6.0 installed, and a JDK 17 or later in `JAVA_HOME` for the C# generator.
3. Build, run, test:

   ```powershell
   dotnet build MixedDotnet.slnx -p:EqDamlAllowToolInstall=true   # the flag only until the generator is installed
   dotnet App\bin\Debug\net10.0\App.dll                        # Gold from Daml package mixed-dotnet-contracts 0.1.0
   dotnet build daml\test\Contracts.Tests.msbuildproj -t:EqDamlTest   # a line per script: ok, or failed with an error
   ```

   Or open `MixedDotnet.slnx` in Visual Studio 2026 and build the solution. Without .NET, the Daml
   packages alone still build with `dpm build --all`.

## Origin

`daml/` is taken from
[canton-dotnet-sdk-mini-demo](https://github.com/peacefulstudio/canton-dotnet-sdk-mini-demo)
v0.6.0 (Apache-2.0, Copyright 2026 Peaceful Studio OÜ; pristine copy and hashes in
`third_party/canton-dotnet-sdk-mini-demo`). Changes from upstream:

- SDK moved from 3.5.2 to 3.6.0;
- package renamed from the upstream content-hash name to `mixed-dotnet-contracts`;
- the test package depends on the freshly built DAR instead of a copied one, and both
  packages are listed in `multi-package.yaml`;
- the MSBuild projects, the solution and the C# app are ours; the app generates its C# in the
  build instead of using upstream's committed bindings.

`daml/dars/` holds the Splice token API DARs (Apache-2.0, https://github.com/hyperledger-labs/splice).
