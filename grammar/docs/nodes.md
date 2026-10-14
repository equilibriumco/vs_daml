# tree-sitter-daml: nodes

The nodes Daml adds to the Haskell grammar, with their fields. Queries and editors may rely on
these names; changing one is a breaking change of the grammar. Nodes inherited from
tree-sitter-haskell (`signature`, `bind`, `function`, `data_type`, `class`, `instance`, `do`,
`case`, `let`, `import`, comments, pragmas and the rest) keep upstream's names and fields.

The rules live in `tree-sitter-daml/grammar/daml.js`.

## Fields after `with`

| Node | Fields | Notes |
|---|---|---|
| `with_fields` | `field`* (`field`) | The layout block after `with` in templates: one field per line, or several on a line separated by `;` or `,`. Each `field` is upstream's node: `name` (`field_name`, more than one for `a, b : Party`) and `type`. Documentation comments between fields stay in the block; one after the last field follows the block. |

## Records

A record written with `with` gives the same nodes as one written with braces, so queries treat
both alike. In Daml both kinds separate fields with `,` or `;`.

| Written as | Node | Fields |
|---|---|---|
| `data P = P with x : Int`, `\| C with …`, `P {x : Int}` | `record` in `data_constructor` | `name` (`constructor`), `fields` (`fields` with `field`*) |
| `newtype N = N with x : Int` | `record` in `newtype_constructor` | `field`* (`field`) |
| `T with f = v`, `x with f = v`, `(e) with a.b = v` | `record` (expression) | `expression`, `field`* (`field_update`: a name or `field_path` with an optional `expression`, a pun, or `wildcard` for `..`) |
| `V1 with payload -> …`, `(T with a, b)` | `record` (pattern) | `constructor`, `field`* (`field_pattern`) |

`with` binds tighter than application: `create Asset with …` is `create` applied to the record.
The block after `with` ends where its indentation ends, or at `)`, `]`, `then`, `else`, `in`,
`deriving`, `|` between constructors, and the `->` after a pattern.

## Templates

| Node | Fields | Notes |
|---|---|---|
| `template_declaration` | `name` (`name`), `parameters` (`with_fields`)?, `body` (`template_body`)? | `template T with … where …`; `with` may end the header line or start the next one. A template without `with` or without `where` still parses. |
| `template_body` | — | The layout block after `where`; its children are the clauses below. |
| `signatory_clause` | `party` (expression)+ | `signatory a, b`: one `party` per comma-separated expression (a party, a list or a set). |
| `observer_clause` | `party` (expression)+ | As `signatory_clause`. |
| `ensure_clause` | `condition` (expression) | |

A clause still being written (`signatory` with nothing after it) gives an `ERROR` inside
`template_body`; the declarations after the template are not affected.
