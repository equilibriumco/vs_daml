# mixed-dotnet

Daml side of a Daml + C# solution. The C# projects that consume the generated bindings are
added once the MSBuild integration exists; until then this folder only holds the contracts.

Origin: `daml/` is taken from
[canton-dotnet-sdk-mini-demo](https://github.com/peacefulstudio/canton-dotnet-sdk-mini-demo)
v0.6.0 (Apache-2.0, Copyright 2026 Peaceful Studio OÜ; pristine copy and hashes in
`third_party/canton-dotnet-sdk-mini-demo`). Changes from upstream:

- SDK moved from 3.5.2 to 3.6.0;
- package renamed from the upstream content-hash name to `mixed-dotnet-contracts`;
- the test package depends on the freshly built DAR instead of a copied one, and both
  packages are listed in `multi-package.yaml`.

`daml/dars/` holds the Splice token API DARs (Apache-2.0, https://github.com/hyperledger-labs/splice).

Build both packages:

```powershell
dpm build --all
```
