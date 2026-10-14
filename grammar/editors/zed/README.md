# Daml in Zed (sketch)

A Zed extension with the grammar and its queries: highlighting, the outline panel, matching
brackets and indentation. It is a sketch: Zed builds a grammar only from a git commit that holds
the generated parser, which this repository doesn't keep, and there is no language server yet.

## Trying it

1. Unpack `tree-sitter-daml-<version>.tar.gz` from a tree-sitter-daml release and make it a git
   repository: `git init`, `git add -A`, `git commit -m tree-sitter-daml-<version>`.
2. Copy this folder somewhere, and in its `extension.toml` set `repository` to that repository
   (`file:///…`) and `rev` to its commit (`git rev-parse HEAD`).
3. In Zed: *zed: install dev extension*, and pick the copied folder. Zed compiles the grammar to
   WebAssembly itself.

## The queries

| File | Made |
|---|---|
| `languages/daml/highlights.scm` | Generated from `queries/highlights.scm` by `grammar/scripts/editor-queries.mjs`: every pattern, with `@character` → `@string`, `@comment.documentation` → `@comment.doc`, `@keyword.directive` → `@preproc`, `@variable.member` → `@property`, and `@spell` left out. Zed has no capture for module names, so they stay uncoloured. |
| `languages/daml/injections.scm` | Generated: the same as `queries/injections.scm`. |
| `languages/daml/outline.scm` | By hand: templates and their choices, interfaces and their methods, interface instances, exceptions, data types, newtypes, type synonyms, classes and their methods, top-level signatures. |
| `languages/daml/brackets.scm` | By hand: `()`, `[]`, `{}`. |
| `languages/daml/indents.scm` | By hand: the blocks `queries/indents.scm` indents, and brackets. |

## Still to do

- A git repository whose commits hold the generated parser (the plan is a tree-sitter-daml
  repository of its own after 0.1.0); then `repository` and `rev` point there and the extension
  can be published.
- The Daml language server: Zed starts servers from an extension's Rust code compiled to
  WebAssembly; it would run `dpm damlc multi-ide` (or `daml damlc multi-ide`) in the project.
