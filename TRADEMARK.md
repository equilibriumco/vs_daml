# Trademarks

EQ Daml Tools for Visual Studio is an independent project by Equilibrium and is not affiliated with, sponsored by, or endorsed by Digital Asset. Daml and Canton are trademarks of Digital Asset (Switzerland) GmbH. Visual Studio is a trademark of Microsoft Corporation.

The Apache License, Version 2.0, under which Daml's code is published, grants no right to use the
licensor's trade names or trademarks. This file sets how this repository uses them.

## Names

| What | Value |
|---|---|
| Display name | EQ Daml Tools for Visual Studio |
| Short name (options pages, output panes, tool windows, tags) | EQ Daml Tools |
| Description | Daml language support, project system and C# integration for Visual Studio. |
| VSIX Identity Id / Publisher | `Equilibrium.Daml.VisualStudio` / `Equilibrium` |
| NuGet IDs | `Equilibrium.Daml.Sdk`, `Equilibrium.Daml.Build.CSharp`, `Equilibrium.Daml.Templates` |
| Namespaces and assemblies | `Equilibrium.Daml.*` |
| MSBuild properties, items and targets we define | `EqDaml` prefix |
| Diagnostic IDs | `EQD0001`, … |
| Authors, Company, Copyright | Equilibrium |
| Language name, content type, file extension | Daml / `daml` / `.daml`: these name the language and are not ours to rename |

Short form, used in the VSIX description, the installed-product registration and the NuGet
package descriptions:

> Independent Daml tooling by Equilibrium — not affiliated with or endorsed by Digital Asset. Daml is a trademark of Digital Asset (Switzerland) GmbH.

## Rules

1. Equilibrium / EQ leads, Daml only describes. Never `Daml.*`, `DamlTools` without `Equilibrium.`,
   "Daml Studio", "Daml for Visual Studio" or "Visual Studio Daml".
2. Write `Daml` in prose and UI. `DAML` only inside upstream identifiers; `daml` only in
   identifiers, extensions, CLI names and URLs.
3. No Daml, Canton or Digital Asset logos, colors or lookalikes. Never copy images from
   `digitalasset/daml` or daml.com. Never generate an icon; leave a placeholder and flag it.
4. Never "official", "certified", "endorsed", "partner" or "powered by Daml". "Digital Asset"
   appears only in the notices and upstream copyright headers, never in Publisher, Authors or
   Company.
5. Canton in prose only ("works with Canton"), never in names, tags or paths.
6. Nothing upstream gets renamed: the Daml language, the Daml SDK, `dpm`, `damlc`, `daml.yaml`,
   `.daml` and `.dar` files, `DAML_*` environment variables the tools define, daml.com URLs,
   Apache-2.0 code from `digitalasset/daml` and its headers, `tree-sitter-daml`, and third-party
   packages such as `Daml.Runtime`.
7. Unsure? Ask before naming anything.

## Scan before every release

Run these patterns over the repository and review every hit:

```
(?i)daml ?studio|(?i)daml for visual studio|(?i)visual ?studio daml
(?i)powered by daml
(?i)\b(official|certified|endorsed|sponsored|partner)\b     # review each hit
(?i)digital ?asset                                          # notices and upstream headers only
\bDAML\b                                                    # in .md .resx .vsixmanifest .vsct .vstemplate docs/ → must be Daml
^\s*namespace\s+Daml\b|<(RootNamespace|AssemblyName|PackageId)>\s*Daml|Id="Daml
(?i)canton                                                  # prose only, never in names or paths
```

## Icon

The extension has no icon yet. It needs artwork of Equilibrium's own design before the Marketplace
listing; nothing derived from Daml, Canton or Digital Asset material.
