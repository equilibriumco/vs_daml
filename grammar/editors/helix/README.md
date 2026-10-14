# Daml in Helix

For Helix 25.07 or later: from that release Helix, like the tree-sitter CLI and Neovim, lets the
last matching highlight pattern win, which these queries rely on.

1. Unpack `tree-sitter-daml-<version>.tar.gz` from a tree-sitter-daml release somewhere permanent.
   It holds the generated parser, which Helix compiles; this repository doesn't keep it.
   (In a checkout, `npx tree-sitter generate` in `grammar/tree-sitter-daml` makes it too.)
2. Merge [languages.toml](languages.toml) into your `languages.toml` (`~/.config/helix/`, on
   Windows `%AppData%\helix\`) and set the grammar's `path` to the unpacked folder.
3. Copy [queries/daml/](queries/daml/) to `runtime/queries/daml/` in the same folder.
4. `hx --grammar build`, then `hx --health daml` should list the grammar and the queries.

Helix needs a C compiler on `PATH` to build the grammar (on Windows, a Developer prompt of Visual
Studio).

## The queries

| File | Made |
|---|---|
| `highlights.scm` | Generated from `queries/highlights.scm` by `grammar/scripts/editor-queries.mjs`: captures renamed to Helix's theme scopes (`@module` → `@namespace`, `@number` → `@constant.numeric.integer`, `@keyword.conditional` → `@keyword.control.conditional`, …); `@spell` and the patterns that need predicate-only captures (`@_op`, `@_name`) left out. Helix takes a node's highlight away when a later pattern captures the node under a name no theme scope matches, which those names are, so the heuristics that colour a function by its `IO` type or by the operator next to it (`f $ x`, `f . g`) don't come along. |
| `injections.scm` | Generated: the same as `queries/injections.scm`. |
| `locals.scm` | By hand: the definitions of `queries/locals.scm` named `local.definition.<scope>`, the scope Helix colours their references with. |
| `indents.scm` | By hand: `@indent` on the blocks `queries/indents.scm` indents. |
| `textobjects.scm` | By hand: functions and choices (`maf`, `]f`), templates, interfaces, instances and data types (`mat`, `]t`), parameters and fields (`mia`, `]a`), comments (`mac`, `]c`). |

After changing `queries/highlights.scm` or `queries/injections.scm`, run
`node grammar/scripts/editor-queries.mjs --update`; the grammar's tests fail while the copies are stale.

A theme can colour Daml's contract keywords (`with`, `choice`, `controller`, `signatory`, …)
apart from other keywords through the `keyword.contract` scope; without it they look like
keywords.
