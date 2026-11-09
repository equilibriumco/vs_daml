# C# generated from the generator's mini demo

The Daml package of [canton-dotnet-sdk-mini-demo](https://github.com/peacefulstudio/canton-dotnet-sdk-mini-demo),
vendored in `third_party/canton-dotnet-sdk-mini-demo/daml`: two templates that implement the Splice token standard
interfaces, built against the Splice DARs it carries. `csharp/` is what the pinned generator writes for it, byte for byte,
without the generation manifest.

Upstream used to commit these bindings and the tests compared against that copy; it stopped when it moved to build-time
generation, so the baseline is recorded here instead. It guards against generator drift, not against upstream.

| What | Version |
|---|---|
| Daml SDK | 3.7.1 (the copy's `daml.yaml` is moved from upstream's 3.5.2) |
| Generator | `oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.8.0@sha256:002f7382a01e141395d4cf62c6a42696c6778108c592444d9233ec31e26eeb38` |
| Main package ID | `b2fd028a37023d031efa04d0f1a06c88d5e5614083e3fa236f680f95f9a36bf2` |

Used by `RealGenerationUpdateTests` and `DamlTasksOnHostsTests` (`test/Equilibrium.Daml.Build.Tests`), which hold the
generator to it, and by the binding index tests (`test/Equilibrium.Daml.VisualStudio.Roslyn.Tests`), which compile it
without the Splice packages and still find its templates and choices. Regenerate it together with the other binding
fixtures:

```powershell
pwsh test/fixtures/bindings/regenerate.ps1
```
