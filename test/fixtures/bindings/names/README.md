# Generated identifier and namespace fixture

This package tests the pinned emitter's name conversion: a module equal to the namespace
prefix (`Names`), one below it (`Names.More`), an escaped module segment (`Names'.Types`),
apostrophes in declarations and members, and an identifier containing `_u0027`.

`csharp/` is the unmodified output of Daml SDK 3.6.0 and `dpm-codegen-cs` 0.6.0 with namespace
prefix `Names`. The generator digest is the same as the adjacent `kinds` fixture.
The main package ID is `01369120814ed69ddf9c25cbe59194051b717447b9a8ee1d67593aecf4339ec7`.

`BindingFixtureTests` rebuilds the DAR, verifies its module metadata and compares every
generated file. The Roslyn tests compile these files and resolve their symbols back to Daml.
Regenerate both fixtures with `pwsh test/fixtures/bindings/regenerate.ps1`, then review the diff.
