# Daml.Sdk and Daml.Build.CSharp in Visual Studio, checked by hand

A solution with a Daml project on `Daml.Sdk` (`Contracts`), a C# project that only asks it for its DAR (`App`) and a C#
project that gets C# generated from it through `Daml.Build.CSharp` (`Client`), to check in Visual Studio 2026 what the
automated tests can't: how the projects load and look, and what the IDE does with them. The steps and their expected
results are the M3-T04 and M3-T05 checklists in `docs/progress.md`.

The `Directory.*` files here are empty on purpose: they keep the repository's own build settings and central package
versions out, so the solution builds like a user's.

## Set up
1. `pwsh scripts/pack.ps1` — leaves `Daml.Sdk.0.2.0-preview.0.nupkg` and `Daml.Build.CSharp.0.2.0-preview.0.nupkg` in
   `artifacts/packages`, where `nuget.config` here looks for them; `Daml.Runtime` and `Daml.Ledger.Abstractions` come from
   nuget.org. If the version differs (a CI build number), change `Daml.Sdk` in `global.json` and `Daml.Build.CSharp` in
   `Client/Client.csproj` to match.
2. After every new pack, delete `%USERPROFILE%\.nuget\packages\daml.sdk\0.2.0-preview.0` and
   `%USERPROFILE%\.nuget\packages\daml.build.csharp\0.2.0-preview.0`: NuGet keeps the first copy of a version and never
   looks again.
3. dpm with SDK 3.6.0 installed (`dpm version`), as for the other samples, and a JDK 17 or later in `JAVA_HOME` for the
   C# generator. The first build of `Client` on a machine without the pinned generator needs
   `/p:DamlAllowToolInstall=true` once (or `dpm` installing it by hand); after that it builds offline.
4. Open `SdkManual.sln` in Visual Studio 2026; the extension isn't needed for this.

`Contracts/.daml`, `obj` and `bin` are build output and ignored by git.
