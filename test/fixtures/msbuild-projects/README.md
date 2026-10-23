# Sample C# projects with Daml bindings

Shapes of projects that use `Equilibrium.Daml.Build.CSharp`, read by `MSBuildContractTests` in `Equilibrium.Daml.Build.Tests`.
The test takes their properties and their `EqDamlReference`, `EqDamlDar` and `ProjectReference` items as
written, without evaluating MSBuild (so no conditions or imports here), places each project in a
virtual `C:\work\App` folder and snapshots what the build contract makes of it. The Daml projects
and DARs they refer to don't have to exist.

| File | Shape |
|---|---|
| `basic.csproj` | a Daml project from the solution and a vendor DAR, each with a namespace |
| `visual-studio-reference.csproj` | plain project references, as Visual Studio adds them |
| `same-package-two-folders.csproj` | two Daml projects of the same name told apart by a namespace |
| `source-mode.csproj` | generated code kept in the project, offline |
| `misconfigured.csproj` | every setting wrong at once, so one build reports all of it |
