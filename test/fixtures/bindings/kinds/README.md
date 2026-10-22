# C# generated from every kind of Daml declaration

A Daml package of our own (`daml.yaml`, `daml/Kinds/*.daml`) with a template that has fields of several kinds, a key and
choices, a second template, an interface with a choice and its view, a record, a parameterised record, a variant with a
record constructor, a plain one and an empty one, and an enum. `csharp/` is what the pinned generator wrote for it, byte
for byte: the C# a project gets with `<EqDamlReference … Namespace="Fixture" />`, without the generation manifest (which
holds paths of the machine that built it).

The binding index tests (`test/Equilibrium.Daml.VisualStudio.Roslyn.Tests`) compile these files and read the Daml declarations back
from them, so they test against the generator's real output rather than a guess of it.

| What | Version |
|---|---|
| Daml SDK | 3.6.0 (`daml.yaml`) |
| Generator | `oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0@sha256:9f040219936298f085e2ade77fbbc37829df065b879633282aa4628c9a1b6548`, the default of `Equilibrium.Daml.Build.CSharp` |
| Main package ID | `bd2aa85e017dad908dbd9c8103929df3edaa7b0961b675d3ef216644565f9cb1` |

## Keeping it current
`BindingFixtureTests` in `test/Equilibrium.Daml.Build.Tests` (category `MSBuild`, backend `Real`) builds this package and generates
its C# through `Equilibrium.Daml.Sdk` and `Equilibrium.Daml.Build.CSharp`, as a user's solution does, and fails when the result differs from
`csharp/`. After a generator or SDK update, regenerate it with:

```powershell
pwsh test/fixtures/bindings/regenerate.ps1
```

then look at the diff: it is what the binding index has to keep up with.
