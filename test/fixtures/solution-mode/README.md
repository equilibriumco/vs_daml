# Solution mode of the Daml language server, checked by hand

`app/SolutionMode.slnx` has three Daml projects on `Daml.Sdk` and no `multi-package.yaml`: `Contracts` and
`Contracts.Tests` in the solution folder, and `Lib` outside it (`shared/Lib`). `Contracts` uses a type from `Lib`, the
tests use both (data-dependencies on their DARs). It is for what the automated tests can't show: which packages the
server serves, the info bar that offers a `multi-package.yaml`, and Go To Definition across packages before and after.
The steps and their expected results are the M3-T07 checklist in `docs/progress.md`.

The `Directory.*` files here are empty on purpose: they keep the repository's own build settings and central package
versions out, so the projects build like a user's.

## Set up
1. `pwsh scripts/pack.ps1` — leaves `Daml.Sdk.0.3.0-preview.0.nupkg` in `artifacts/packages`, where `nuget.config` here
   looks for it. After every new pack, delete `%USERPROFILE%\.nuget\packages\daml.sdk\0.3.0-preview.0`: NuGet keeps the
   first copy of a version and never looks again (`pack.ps1` warns when it holds another copy).
2. dpm with SDK 3.6.0 installed.
3. Build once (the data-dependencies are the DARs): `dotnet build app\SolutionMode.slnx`, or Build Solution in Visual
   Studio.

`app/multi-package.yaml`, which the offer creates, is ignored by git; delete it to see the offer again. "Don't ask again"
writes `app\.vs\DamlVisualStudio\multi-package-offer.declined`; delete that to be asked again. `.daml`, `obj` and `bin`
are build output.
