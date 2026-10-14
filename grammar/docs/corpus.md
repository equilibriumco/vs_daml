# The real-world corpus

Checked 2026-10-14 by `grammar/scripts/corpus-gate.mjs` with tree-sitter CLI 0.27.1, grammar at `baf3469`,
corpus collected 2026-10-14T09:29:34Z (`grammar/test/real-world/manifest.json`).

**Gate: passed.** 760 of 764 files (99.5%) parse without errors;
at least 99% are required, and every other file must be a known exception below.

## Sources

| Source | Pinned at | SDK | License | Files | Without errors |
|---|---|---|---|---:|---:|
| daml | main `682ba2e` | 3.x (main, after 3.4.11) | Apache-2.0 | 258 | 256 |
| daml-finance | main `5ba1463` | 2.10.0 | Apache-2.0 | 312 | 311 |
| splice | 0.9.1 `ec6ec6f` | 3.5.2 | Apache-2.0 | 185 | 185 |
| samples | working tree | 3.6.0 | Apache-2.0 | 9 | 8 |

## Daml constructs

How often each construct occurs, counted by its nodes in the parse trees.

| Construct | daml | daml-finance | splice | samples | Total |
|---|---:|---:|---:|---:|---:|
| template | 115 | 102 | 108 | 7 | 332 |
| choice | 166 | 149 | 344 | 4 | 663 |
| interface | 9 | 73 | 23 | 1 | 106 |
| interface instance | 6 | 232 | 53 | 4 | 295 |
| exception | 1 | 1 | 0 | 0 | 2 |
| contract key | 8 | 6 | 0 | 2 | 16 |
| try … catch | 1 | 1 | 1 | 0 | 3 |
| record | 975 | 2895 | 4561 | 46 | 8477 |

## Known exceptions

Files that don't parse, and why (`grammar/corpus-exceptions.json`).

| File | Why |
|---|---|
| `daml/sdk/compiler/damlc/daml-prim-src/GHC/Classes.daml` | A compiler primitive run through the C preprocessor before it is parsed: its export list has `#ifdef DAML_BIGNUMERIC` inside the parentheses. User code doesn't do this. |
| `daml/sdk/compiler/damlc/daml-prim-src/GHC/Tuple.daml` | A compiler primitive that declares the built-in tuple types, `data (a,b) = (a,b)`; only the compiler's own primitives may. |
| `daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml` | A `do` statement that starts with a negative number in parentheses, `(-0.052) === …`, inherited from tree-sitter-haskell: `(-1) + 1` as a statement fails without any Daml construct too. |
| `samples/samples/broken/daml/SyntaxError.daml` | Deliberately broken: this repository's sample of a file with a syntax error (a `with` without fields and no `where`). |
