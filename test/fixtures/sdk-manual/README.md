# Daml.Sdk in Visual Studio, checked by hand

A solution with a Daml project on `Daml.Sdk` (`Contracts`) and a C# project that references it (`App`), to check in
Visual Studio 2026 what the automated tests can't: how the project loads and looks, and what the IDE does with it.
The steps and their expected results are the M3-T04 checklist in `docs/progress.md`.

## Set up
1. `pwsh scripts/pack.ps1` — leaves `Daml.Sdk.0.2.0-preview.0.nupkg` in `artifacts/packages`, where `nuget.config` here
   looks for it. If the version differs (a CI build number), change `Daml.Sdk` in `global.json` to match.
2. After every new pack, delete `%USERPROFILE%\.nuget\packages\daml.sdk\0.2.0-preview.0`: NuGet keeps the first copy of a
   version and never looks again.
3. dpm with SDK 3.6.0 installed (`dpm version`), as for the other samples.
4. Open `SdkManual.sln` in Visual Studio 2026; the extension isn't needed for this.

`Contracts/.daml`, `obj` and `bin` are build output and ignored by git.
