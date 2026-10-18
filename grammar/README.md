# grammar/

The tree-sitter grammar for Daml, its checks and builds. The grammar itself, as it would be
published, is [`tree-sitter-daml/`](tree-sitter-daml/README.md); this folder holds what works on it.

| Path | What |
|---|---|
| `tree-sitter-daml/` | The grammar package: `grammar.js` (upstream's rules in `grammar/*.js`, Daml's in `grammar/daml.js`), the external scanner `src/scanner.c`, `queries/`, tests (`test/corpus`, `test/highlight`, `test/tags`, `test/queries`). MIT. |
| `UPSTREAM.md` | The tree-sitter-haskell commit the fork starts from, what was changed, how to take upstream changes. |
| `tree-sitter-runtime.txt` | The tree-sitter runtime built into the fuzzer and the native library: the release of the CLI pinned in `tree-sitter-daml/package.json`. |
| `docs/nodes.md` | The nodes Daml adds and their fields: the contract for queries and editors. |
| `docs/corpus.md`, `corpus-sources.psd1`, `corpus-exceptions.json` | The real-world corpus: pinned sources, the gate's last report, the files allowed to fail and why. |
| `docs/baseline.md` | What the unchanged Haskell grammar made of the corpus, before any Daml rule. |
| `scripts/` | Node scripts with their tests (`*.test.mjs`), below. |
| `fuzz/` | The libFuzzer harness and its builds. |
| `editors/` | Setups for Neovim, Helix and Zed; the Helix and Zed queries generated from `queries/`. |
| `../native/` | `daml_ts.dll`: the runtime and the grammar in one Windows library (x64, ARM64). |

## Commands

From the repository root unless said otherwise; the grammar's own commands
(`npm ci`, `npx tree-sitter generate`, `npx tree-sitter test`) run in `tree-sitter-daml/`.

```powershell
pwsh scripts/test.ps1 -Category Grammar                          # tree-sitter test and fuzz, the scripts' tests, the corpus gate
pwsh scripts/test.ps1 -Category All -Milestone M4 -RequiredTools # the same as a gate: a missing corpus or tool fails
pwsh scripts/collect-corpus.ps1                                  # the real-world corpus into test/real-world/ (not committed)
node grammar/scripts/corpus-gate.mjs                             # ≥ 99% of the corpus without errors → docs/corpus.md
node grammar/scripts/edit-properties.mjs --cases 1000 --seed N   # incremental parsing after random edits = a full parse
node grammar/scripts/perf.mjs                                    # parsing speed against its budgets, on a quiet machine
node grammar/scripts/query-snapshots.mjs --update                # rewrites the folds/indents snapshots; review the diff
node grammar/scripts/editor-queries.mjs --update                 # regenerates the Helix and Zed queries from queries/
pwsh grammar/fuzz/build.ps1                                      # the fuzzer with ASan (MSVC); Linux: grammar/fuzz/build.sh with UBSan
node grammar/scripts/fuzz-seeds.mjs <dir>                        # seeds for it: corpus tests and the real-world files
pwsh native/build.ps1 [-Package]                                 # daml_ts.dll for x64 and ARM64, its smoke test, release zips
```

## What keeps the grammar honest

- **Corpus tests** (`tree-sitter-daml/test/corpus/`): the Daml constructs in `daml/`, upstream's
  Haskell tests in `haskell/` with Daml's colons. Highlight and tag tests assert captures in
  place (`-- <- @capture`); folds and indents are compared with reviewed snapshots.
- **The real-world corpus gate:** at least 99% of the collected files parse without errors, every
  other one is a listed exception with a reason, and every Daml construct occurs.
- **Incremental parsing:** after random edits to real files, the incrementally parsed tree is the
  tree of a full parse (when the edited text has no errors), and undoing the edits gives the
  original tree back. A node's end may move over whitespace only, which the layout scanner
  inherited from tree-sitter-haskell does depending on the subtrees it reuses.
- **Fuzzing:** every seed, then random inputs, through parsing, edits and incremental parsing
  under AddressSanitizer and UndefinedBehaviorSanitizer, with the same properties as above.
- **Speed:** 5 000 lines parse in ≤ 40 ms and an edit reparses in ≤ 25 ms (medians); that is
  what the inherited grammar gives, its GLR branches keep subtrees from being reused.

## CI

- `.github/workflows/grammar.yml`, on changes under `grammar/` and `native/`: the tests, `tree-sitter
  fuzz`, the corpus gate, 300 incremental-parsing cases (new seed each run), every seed under the
  sanitizers and a minute of fuzzing, the parsing speed (reported only: shared runners are too
  noisy), and the release artifacts through `grammar-artifacts.yml`.
- `grammar-nightly.yml`, run by hand (Actions → Grammar nightly → Run workflow): ten minutes of fuzzing
  and 3 000 incremental-parsing cases with a new seed, which often meet the known layout bug in
  `UPSTREAM.md`.
- `grammar-artifacts.yml`: `daml_ts.dll` for x64 and ARM64 (smoke test on x64), the WebAssembly
  module (the CLI fetches wasi-sdk), a source archive with the generated parser.

## Releasing

1. Set the version in `tree-sitter-daml/tree-sitter.json` (`metadata.version`); the library and
   the archives take it from there.
2. Push a tag `tree-sitter-daml-v<version>`. `grammar-release.yml` checks that the tag names that
   version, builds the artifacts and creates a draft GitHub release with the two Windows zips,
   the WebAssembly module and the source archive.
3. Review the draft and publish it.

Running the release workflow by hand builds the same files without a release, to try it first.

## Taking upstream changes

[`UPSTREAM.md`](UPSTREAM.md) lists what the fork changed and how to rebase onto a newer
tree-sitter-haskell. A new tree-sitter CLI means a new runtime pin as well: update
`tree-sitter-daml/package.json` and `tree-sitter-runtime.txt` together (a test fails when they
disagree), then run the Grammar category, the fuzzer and the native build.
