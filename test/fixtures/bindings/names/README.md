# Generated identifier and namespace fixture

This package tests the pinned emitter's name conversion: a module equal to the namespace
prefix (`Names`), one below it (`Names.More`), an escaped module segment (`Names'.Types`),
apostrophes in declarations and members, and an identifier containing `_u0027`.

`csharp/` is the unmodified output of Daml SDK 3.7.1 and `dpm-codegen-cs` 0.8.0 with namespace
prefix `Names`. The generator digest is the same as the adjacent `kinds` fixture.
The main package ID is `e194b68a71c9665ced6e89582ec69acc38d4e9afac52386a607c819eacadadc9`.

`BindingFixtureTests` rebuilds the DAR, verifies its module metadata and compares every
generated file. The Roslyn tests compile these files and resolve their symbols back to Daml.
Regenerate both fixtures with `pwsh test/fixtures/bindings/regenerate.ps1`, then review the diff.
