import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { splitQuery, captures, renameCaptures, helixHighlights, helixScope, generatedFiles } from './editor-queries.mjs';

const grammarDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const packageDir = join(grammarDir, 'tree-sitter-daml');
const helixDir = join(grammarDir, 'editors', 'helix', 'queries', 'daml');
const cli = join(packageDir, 'node_modules', 'tree-sitter-cli', 'cli.js');
const readQuery = path => readFileSync(path, 'utf8').replace(/\r\n/g, '\n');

// Brackets, semicolons and `@` inside strings and comments must not split a pattern or look
// like captures.
const tricky = `; a comment with (parens) and @fake
"(" @punctuation.bracket

((variable) @_x
  ; inside: ] @nope
  (#eq? @_x "a;b)@c"))
[
  "if"
  "then"
] @keyword.conditional
(comment)+ @comment
`;

test('a query splits into its patterns and the text between them, losing nothing', () => {
  const segments = splitQuery(tricky);

  assert.equal(segments.map(s => s.text).join(''), tricky);
  assert.deepEqual(segments.filter(s => s.kind === 'pattern').map(s => captures(s.text)), [
    ['punctuation.bracket'],
    ['_x', '_x'],
    ['keyword.conditional'],
    ['comment'],
  ]);
  for (const file of readdirSync(join(packageDir, 'queries'))) {
    const source = readQuery(join(packageDir, 'queries', file));
    assert.equal(splitQuery(source).map(s => s.text).join(''), source, file);
  }
});

test('captures are renamed or removed, strings and comments left alone', () => {
  const pattern = '((name) @module @spell ; @module\n  (#eq? @module "@module"))';

  assert.equal(
    renameCaptures(pattern, n => (n === 'module' ? 'namespace' : n === 'spell' ? null : n)),
    '((name) @namespace ; @module\n  (#eq? @namespace "@module"))');
});

test("Helix's highlights rename captures and leave out what would take a highlight away", () => {
  const source = `(integer) @number

; Spell checking.
(comment) @spell

(comment) @comment

; Function application.
((variable) @function.call
  .
  (operator) @_op
  (#eq? @_op "$"))

(module_id) @module
`;

  assert.equal(helixHighlights(source), `(integer) @constant.numeric.integer

(comment) @comment

(module_id) @namespace
`);
});

test("every highlight in Helix's copy reaches a Helix scope, with predicates Helix's highlights accept", () => {
  const helix = readQuery(join(helixDir, 'highlights.scm'));
  const names = splitQuery(helix).filter(s => s.kind === 'pattern').flatMap(s => captures(s.text));
  const predicates = new Set(helix.match(/#[\w-]+[?!]/g));
  const accepted = ['#eq?', '#not-eq?', '#any-eq?', '#any-not-eq?', '#match?', '#not-match?', '#any-match?',
    '#any-not-match?', '#any-of?', '#not-any-of?'];

  assert.ok(names.length > 50, `only ${names.length} captures`);
  assert.deepEqual(names.filter(n => helixScope(n) === null), []);
  assert.deepEqual([...predicates].filter(p => !accepted.includes(p)), []);
});

test("the locals written for Helix define names with Helix scopes", () => {
  const locals = readQuery(join(helixDir, 'locals.scm'));
  const definitions = captures(locals).filter(n => n.startsWith('local.definition'));

  assert.ok(definitions.length > 0);
  for (const name of definitions) {
    assert.ok(helixScope(name.slice('local.definition.'.length)), name);
  }
});

test('the generated query files are current', () => {
  for (const { path, content } of generatedFiles()) {
    assert.equal(readQuery(path), content, `${path} is stale: run node grammar/scripts/editor-queries.mjs --update`);
  }
});

test("every query for Helix compiles against the grammar", () => {
  const fixture = join(packageDir, 'test', 'highlight', 'template.daml');
  for (const file of readdirSync(helixDir)) {
    const run = spawnSync(process.execPath, [cli, 'query', join(helixDir, file), fixture], { cwd: packageDir, encoding: 'utf8' });
    assert.equal(run.status, 0, `${file}: ${run.stderr}`);
  }
});
