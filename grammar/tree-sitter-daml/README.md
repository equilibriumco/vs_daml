# tree-sitter-daml

A [tree-sitter](https://tree-sitter.github.io/) grammar for [Daml](https://docs.digitalasset.com/),
the smart-contract language of the Canton Network. It starts as a fork of
[tree-sitter-haskell](https://github.com/tree-sitter/tree-sitter-haskell) (Daml's syntax is
Haskell's plus contract declarations), keeping its layout scanner, and adds Daml's own
constructs: templates, choices, interfaces and `with` records.

Status: work in progress (`0.1.0-dev`). The grammar still parses Haskell; the Daml
constructs, queries for editors and the native and WASM builds come next. Where it comes from
and how to take upstream changes: [`../UPSTREAM.md`](../UPSTREAM.md).

## Working on it

```sh
npm ci                     # installs the pinned tree-sitter CLI
npx tree-sitter generate   # writes src/parser.c and friends from grammar.js (not committed)
npx tree-sitter test       # runs test/corpus; needs a C compiler (MSVC, clang or gcc)
npx tree-sitter parse ../../samples/single-package/daml/Main.daml
```

`test/corpus/haskell/` holds the tests inherited from tree-sitter-haskell. They keep passing as
long as the Daml changes leave Haskell syntax alone; a test that stops applying to Daml moves out
with a note in `UPSTREAM.md`.

## License

MIT, see [LICENSE](LICENSE); the original tree-sitter-haskell copyright notice is kept there.