# tree-sitter-daml in editors

| Editor | Setup | Queries |
|---|---|---|
| Neovim 0.11+ | [neovim/README.md](neovim/README.md) | the grammar's own `queries/` |
| Helix 25.07+ | [helix/README.md](helix/README.md), [helix/languages.toml](helix/languages.toml) | [helix/queries/daml/](helix/queries/daml/): highlights and injections generated from `queries/`, the rest written for Helix |

Every editor compiles the grammar from its generated parser (`src/parser.c`), which this
repository doesn't keep. Each release has it in `tree-sitter-daml-<version>.tar.gz`; in a
checkout, `npx tree-sitter generate` in `grammar/tree-sitter-daml` makes it.

Highlighting needs an editor that lets the last matching pattern win, as the tree-sitter CLI the
grammar is tested with does.

These setups follow each editor's documentation and are not tested in this repository's CI.
