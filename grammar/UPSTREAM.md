# tree-sitter-daml: upstream

`grammar/tree-sitter-daml` is a fork of **tree-sitter-haskell**.

| | |
|---|---|
| Repository | https://github.com/tree-sitter/tree-sitter-haskell |
| Release | `v0.24.1` |
| Commit | `97288e585b0bd44199720280d067fa35bacd81ff` |
| Taken | 2026-10-14 |
| License | MIT (kept in `tree-sitter-daml/LICENSE`) |
| tree-sitter CLI | `0.27.1`, pinned in `tree-sitter-daml/package.json` (upstream asked for `^0.27.0`) |

## What was taken

- `grammar.js`, `grammar/*.js`, `src/scanner.c`, `src/unicode.h`, `queries/*.scm`: as they were.
- `test/corpus/*.txt`: moved to `test/corpus/haskell/`, inputs rewritten to Daml's colons (below).

## What was changed

- The language is `daml`: `name: 'daml'` and the root rule `daml` (was `haskell`) in `grammar.js`;
  the scanner's five exported functions `tree_sitter_daml_external_scanner_*` (were
  `tree_sitter_haskell_external_scanner_*`); the root node `(daml` in the inherited test
  expectations (was `(haskell`).
- **Daml's colons** (Haskell's swapped: `:` annotates a type, `::` is list cons):
  `_colon2` in `grammar/lexeme.js` is `:` (the name kept to keep merges simple); in `src/scanner.c` a
  lone `:` is reserved (`lex_symop`), `::` is an ordinary operator and so a constructor operator
  (`valid_symop_two_chars`), and the data-head lookahead stops at a lone `:` (`ctr_top`);
  `queries/highlights.scm` highlights `":"` (was `"::"`). Operators like `:|` or `:::` are unchanged.
- The inputs of the inherited tests were rewritten by `grammar/scripts/swap-colons.mjs` (exactly `:`
  and `::` swapped, also when qualified, `A.:` → `A.::`; strings, characters and comments untouched).
  Expected trees name nodes, not colons, so all of them stayed as they were and all still pass; no
  test had to leave `test/corpus/haskell/`.
- **Daml's constructs** live in our own module, `grammar/daml.js` (templates and their clauses,
  keys, choices, interfaces and their instances, exceptions and `try`/`catch`, the `with` blocks of
  fields, records with `with`). Upstream files are touched only to plug it in, each place marked
  with a `Daml:` comment: `grammar.js` requires the module and spreads it into the rules
  (`...daml`); `declaration` in `grammar/module.js` lists `template_declaration`,
  `interface_declaration` and `exception_declaration` first; `expression` in `grammar/exp.js` has
  `try` next to `case`; the record rules (`_record_fields` in `grammar/data.js`, `_exp_record` in
  `grammar/exp.js`, `_pat_record` in `grammar/pat.js`) take `with` and a block as an alternative to
  braces, and accept `;` between fields in braces; `_exp_signature` (`grammar/exp.js`),
  `field_update` (`grammar/exp.js`) and `field_pattern` (`grammar/pat.js`) have an optional phantom
  before their `:` or `=`. `grammar/externals.js` has five more externals: a layout start,
  `_cmd_layout_start_with`, after `_cmd_layout_start_quote`; the phantoms `_phantom_colon` and
  `_phantom_equals` after `_phantom_deriving`; and `_cmd_choice_body` after `_consym`. Their nodes
  are listed in `grammar/docs/nodes.md`; our tests are under `test/corpus/daml/`.
- **The `with` block in the scanner** (`src/scanner.c`): a symbol `START_WITH` (mirroring the new
  external) and a context sort `WithLayout` (below `MultiWayIfLayout`, so it gets layout
  semicolons). Inside it a `,` or `=` doesn't end the block in parentheses
  (`end_layout_texp_closer`), but an `=` that can't give a field its value ends it anywhere (symbol
  `EQUALS`, the phantom, as after the `..` of `f Con with a; .. = …`); a lone `:` that can't
  annotate the expression before it ends it (`end_layout_colon`, symbol `COLON`, as in
  `key K with a; b : T`); an arrow that can't continue it does (`end_layout_arrow`), and
  `deriving` ends it as it ends GADT layouts (`end_layout_deriving`). It isn't started at all when
  the next word is `controller` followed by a party, a choice's empty `with`
  (`controller_follows`, in `valid_layout_start`). The debug names of the
  context sorts were reordered to match the enum (upstream had `quote` and `multi_way_if` swapped).
- **Highlights** (`queries/highlights.scm`) keep upstream's order: when several patterns capture
  a node, the last one wins (the tree-sitter CLI and Neovim; Helix needs the opposite order). Our
  changes: a note on that at the top; `let`, `in`, `class`, `instance`, `data`, `newtype`,
  `type` and `deriving` are `@keyword.declaration` (as the Visual Studio extension colours them);
  upstream's `(variable) @type`, which overrode every variable, is `(type/variable) @type`;
  `(comment) @spell` moved before `(comment) @comment`, so that the comment highlight wins; Daml's
  debug and error functions added to those lists; Daml's own captures in a last section. Our
  highlight tests are under `test/highlight/`.
- **The body of a choice in the scanner** (`src/scanner.c`): a symbol `CHOICE_BODY` (mirroring
  `_cmd_choice_body`, before `UPDATE`), emitted with zero width before a `do` where a choice's body
  may start (`choice_body`, first in `interior`). Haskell lets a `do` block be an argument, so
  without it `controller a :: b` followed by `do` would read as `b do …`.
- Our own `package.json` (private, only the pinned CLI), `tree-sitter.json` (file type `.daml`,
  scope `source.daml`), `.gitignore`, `.gitattributes`, `.editorconfig`, `README.md`; our
  copyright line added to `LICENSE`.

## What was left out

- Generated sources (`src/parser.c`, about 20 MB, `src/grammar.json`, `src/node-types.json`,
  `src/tree_sitter/`): regenerated by `tree-sitter generate`, ignored by git.
- Bindings for Node, Rust, Python, Go, Swift and C, with their build files (`binding.gyp`,
  `Cargo.*`, `setup.py`, `pyproject.toml`, `go.*`, `Package.*`, `CMakeLists.txt`, `Makefile`):
  not used here. The native library for Visual Studio and the WASM build come with their own
  build later; an npm package is optional.
- `examples/`, upstream's GitHub workflows and README.

## Taking upstream changes

1. Clone tree-sitter-haskell at the new release tag next to this repository.
2. Diff the upstream release against the one recorded above (`git diff v0.24.1 <new tag> -- grammar.js grammar src/scanner.c src/unicode.h queries test/corpus`).
3. Apply that diff to `tree-sitter-daml` (test files under `test/corpus/haskell/`; run
   `node grammar/scripts/swap-colons.mjs` on test files that are new upstream, once, since swapping
   twice gives the original back), redo the renames
   above where the diff touches them, and keep the Daml rules (`grammar/daml.js`, the lines that
   plug it in and the scanner's `with` block; `grep -n Daml src/scanner.c grammar/*.js` finds them,
   apart from the two `daml` lines in `grammar.js`).
4. `npx tree-sitter generate` and `npx tree-sitter test` must pass; then update the table above.