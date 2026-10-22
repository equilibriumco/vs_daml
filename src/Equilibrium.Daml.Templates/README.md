# Equilibrium.Daml.Templates

`dotnet new` templates for Daml solutions built with MSBuild.

```
dotnet new install Equilibrium.Daml.Templates
dotnet new daml-contracts -n Contracts      # a Daml package and a package of Daml Script tests
dotnet new daml-dotnet-app -n MyApp         # the same plus a C# console app on the generated C#
```

The projects use `Equilibrium.Daml.Sdk` and `Equilibrium.Daml.Build.CSharp` of the same release. Options:
`--daml-sdk` sets the Daml SDK in `daml.yaml`; `daml-dotnet-app` also takes `--allow-tool-install`
(true by default), which lets the first build download the C# generator.

Requires dpm 1.0.22 or later with Daml SDK 3.5.x or 3.6.x; `daml-dotnet-app` also needs JDK 17 or later
and the .NET 10 SDK.

Details: [user guide](https://github.com/equilibriumco/vs_daml/blob/main/docs/user-guide.md).

Independent Daml tooling by Equilibrium — not affiliated with or endorsed by Digital Asset. Daml is a
trademark of Digital Asset (Switzerland) GmbH.
