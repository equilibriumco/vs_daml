# tree-sitter-daml

A [tree-sitter](https://tree-sitter.github.io/) grammar for [Daml](https://docs.digitalasset.com/),
the smart-contract language of the Canton Network. It is a fork of
[tree-sitter-haskell](https://github.com/tree-sitter/tree-sitter-haskell) 0.24.1 (Daml's syntax is
Haskell's plus contract declarations), keeping its layout scanner, with Daml's colons (`:` for
types, `::` for cons) and its own constructs:

- templates with `with` fields, `signatory`, `observer`, `ensure`, `key` and `maintainer`;
- choices, with `nonconsuming`/`preconsuming`/`postconsuming`, parameters, `observer`,
  `controller` and a `do` body;
- interfaces (`requires`, `viewtype`, methods, choices) and interface instances;
- records written with `with` (`T with f = v`, in patterns and in `data`), parsed into the same
  nodes as records in braces;
- exceptions (`message`) and `try`/`catch`.

Version 0.1.0. 760 of the 764 Daml files of the real-world corpus (the Daml standard library,
daml-finance, Splice and this repository's samples) parse without errors; the four others are
listed with their reasons in [`../docs/corpus.md`](../docs/corpus.md). The nodes Daml adds, and
their fields, are in [`../docs/nodes.md`](../docs/nodes.md).

## Queries

| File | For |
|---|---|
| `queries/highlights.scm` | Highlighting, with Neovim's capture names; the last matching pattern wins, as in the tree-sitter CLI, Neovim, Helix 25.07+ and Zed. Daml's declarations are `@keyword.declaration`, its contract words (`with`, `choice`, `controller`, `signatory`, …) `@keyword.contract`. |
| `queries/locals.scm` | Scopes, definitions and references, so that a use is coloured like its definition. |
| `queries/tags.scm` | Templates, interfaces, choices, types, classes and functions for code navigation. |
| `queries/folds.scm`, `queries/indents.scm` | Folding and indentation (nvim-treesitter's conventions). |
| `queries/injections.scm` | Comments, and the languages of Haskell quasi-quotes. |

Helix and Zed name their captures differently: their copies, and setups for Neovim, Helix and
Zed, are in [`../editors/`](../editors/README.md).

## Using it

The generated parser (`src/parser.c`) isn't kept in the repository; every release has it in
`tree-sitter-daml-<version>.tar.gz`, which is what editors build from.

- **tree-sitter CLI:** `npm ci`, `npx tree-sitter generate`, then `npx tree-sitter parse <file>`,
  `npx tree-sitter highlight <file>` or `npx tree-sitter playground` (with the release's
  `tree-sitter-daml-<version>.wasm` renamed to `tree-sitter-daml.wasm` next to `tree-sitter.json`).
- **C, on Windows:** `daml_ts-<version>-windows-x64.zip` (and `-arm64`, built but not tested on
  ARM64 hardware) holds `daml_ts.dll`, the tree-sitter runtime and the grammar in one library. It
  exports the runtime's C API and `tree_sitter_daml` (`include/tree_sitter/api.h`,
  `include/tree_sitter/tree-sitter-daml.h`); load it by its full path and check
  `ts_language_abi_version` before use. Strings and arrays the API returns for the caller to
  free come from the C runtime's heap (`free`).
- **Web:** `tree-sitter-daml-<version>.wasm` loads in web-tree-sitter of the same version as the
  pinned runtime (`../tree-sitter-runtime.txt`).

## Working on it

```sh
npm ci                     # the pinned tree-sitter CLI
npx tree-sitter generate   # src/parser.c and friends from grammar.js
npx tree-sitter test       # corpus, highlight and tag tests; needs a C compiler
```

The grammar's other checks (the real-world corpus, incremental parsing after random edits,
fuzzing under sanitizers, parsing speed), the native and WebAssembly builds and releasing are
described in [`../README.md`](../README.md). Where the grammar comes from and how to take
upstream changes: [`../UPSTREAM.md`](../UPSTREAM.md).

## License

MIT, see [LICENSE](LICENSE); the original tree-sitter-haskell copyright notice is kept there.
