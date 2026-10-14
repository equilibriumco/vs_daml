// Tests of parse-report.mjs: reading the CLI's output, finding the error regions and naming the
// Daml construct each one is in. Run with `node --test grammar/scripts/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseCliOutput, outermostRegions, classify, summarize, renderReport } from './parse-report.mjs';

const output = [
  '(daml [0, 0] - [9, 0]',
  '  (header [0, 0] - [0, 17]',
  '    (module [0, 7] - [0, 11]))',
  '  (ERROR [3, 0] - [6, 20]',
  '    (ERROR [4, 2] - [4, 6])',
  '    (MISSING "where" [5, 3] - [5, 3]))',
  '  (ERROR [8, 4] - [8, 5]))',
  'C:/corpus/a/Main.daml\tParse:    0.68 ms\t  1425 bytes/ms\t(ERROR [3, 0] - [6, 20])',
  // A file without errors gets its tree but no summary line.
  '(daml [0, 0] - [2, 0]',
  '  (header [0, 0] - [0, 13]))',
].join('\n');

test('every tree of the output comes back in order, with its error and missing nodes', () => {
  const trees = parseCliOutput(output);

  assert.equal(trees.length, 2);
  assert.equal(trees[0].file, 'C:/corpus/a/Main.daml', 'a failed parse names its file');
  assert.equal(trees[1].file, undefined, 'a clean one does not');
  assert.equal(trees[0].errors.length, 4);
  assert.deepEqual(trees[0].errors[2], { kind: 'MISSING', start: { row: 5, column: 3 }, end: { row: 5, column: 3 } });
  assert.deepEqual(trees[1].errors, []);
});

test('a file the parser could not recover gets a tree whose root is the error', () => {
  const trees = parseCliOutput([
    '(ERROR [0, 0] - [3, 0]',
    '  (ERROR [1, 2] - [1, 3]))',
    'C:/corpus/c/Lost.daml\tParse:    0.20 ms\t  900 bytes/ms\t(ERROR [0, 0] - [3, 0])',
    '(daml [0, 0] - [1, 0])',
  ].join('\n'));

  assert.equal(trees.length, 2);
  assert.equal(trees[0].file, 'C:/corpus/c/Lost.daml');
  assert.deepEqual(outermostRegions(trees[0].errors).map(r => r.start.row), [0]);
});

test('errors inside another error are left out of the regions', () => {
  const [main] = parseCliOutput(output);

  const regions = outermostRegions(main.errors);

  assert.deepEqual(regions.map(r => r.start.row), [3, 8]);
});

test('a region is placed at its first innermost error, where the parse actually went wrong', () => {
  const [main] = parseCliOutput(output);

  const regions = outermostRegions(main.errors);

  // [3,0]-[6,20] holds [4,2]-[4,6] and a MISSING at [5,3]: the earliest innermost one counts.
  assert.deepEqual(regions[0].anchor, { row: 4, column: 2 });
  // Nothing inside [8,4]-[8,5]: it is its own place.
  assert.deepEqual(regions[1].anchor, { row: 8, column: 4 });
});

const source = `module Main where

template Asset
  with
    owner : Party
  where
    signatory owner
    observer owner
    key owner : Party
    maintainer key

    nonconsuming choice Look : ()
      controller owner
      do pure ()

    choice Give : ContractId Asset
      with
        newOwner : Party
      controller owner
      do create this with
           owner = newOwner

interface Sellable where
  viewtype SellableView

data Record = Record with
    field : Int

exception Oops
  with
    why : Text
  where
    message why

template Old
  with
    p : Party
  where
    controller p can
      Go : ()
        do pure ()

f x = x ++ ""

mkInteger : Bool   -- non-negative?
         -> [Int]
         -> Integer

merge : forall k a.
    Ord k
    => k -> a

data ArithmeticError = ArithmeticError { message : Text }

pattern Nothing : Maybe a

data NatSyn (n : Nat)

data Key = Key
  with
    provider : Party

allocate
  : Party -> Text
  -> Script ()
allocate p t = do
  result <- submit p $ exerciseCmd cid Transfer_Send
    with
      sender = p
      context
`.split('\n');

const row = text => source.findIndex(l => l.includes(text));

test('an error is named by the nearest Daml construct around it', () => {
  const cases = [
    ['template Asset', 'template'],
    ['owner : Party', 'template'], // the template's own with: bare `with` lines don't count
    ['signatory owner', 'template clause: signatory'],
    ['observer owner', 'template clause: observer'],
    ['key owner', 'contract key'],
    ['maintainer key', 'contract key'],
    ['nonconsuming choice Look', 'choice'],
    ['do pure ()', 'choice'],
    ['newOwner : Party', 'choice'],
    ['controller owner', 'choice'],
    ['do create this with', 'record with expression'],
    ['owner = newOwner', 'record with expression'],
    ['interface Sellable', 'interface'],
    ['viewtype SellableView', 'interface'],
    ['data Record = Record with', 'record with declaration'],
    ['field : Int', 'record with declaration'],
    ['exception Oops', 'exception'],
    ['message why', 'exception'],
    ['controller p can', 'controller … can (Daml 1.x)'],
    ['f x = x', 'other'],
    // Daml's `:` is Haskell's `::`: signatures, record fields and pattern synonyms.
    ['mkInteger : Bool', 'type signature with `:`'],
    ['-> [Int]', 'type signature with `:`'],
    ['=> k -> a', 'type signature with `:`'],
    ['{ message : Text }', 'type signature with `:`'],
    ['pattern Nothing', 'type signature with `:`'],
    ['data NatSyn (n : Nat)', 'type signature with `:`'],
    ['  : Party -> Text', 'type signature with `:`'],
    ['  -> Script ()', 'type signature with `:`'],
    // ...but a field under `with` belongs to its declaration.
    ['p : Party', 'template'],
    // The same declaration with its `with` on the next line.
    ['provider : Party', 'record with declaration'],
    // A `with` on a line of its own after an expression is a record update or construction.
    ['sender = p', 'record with expression'],
    ['      context', 'record with expression'],
  ];
  for (const [text, construct] of cases) {
    assert.equal(classify(source, row(text)).construct, construct, `line "${text}"`);
  }
});

test('an unclassified error says what its line starts with', () => {
  assert.equal(classify(source, row('f x = x')).detail, 'f');
});

test('the summary counts first errors per construct and clean files per source', () => {
  const results = [
    { file: 'a.daml', source: 'one', regions: [{ construct: 'template' }, { construct: 'choice' }] },
    { file: 'b.daml', source: 'one', regions: [{ construct: 'template' }] },
    { file: 'c.daml', source: 'two', regions: [] },
  ];

  const summary = summarize(results);

  assert.deepEqual(summary.totals, { files: 3, clean: 1, withErrors: 2, regions: 3 });
  assert.deepEqual(summary.constructs[0], { construct: 'template', firstErrorFiles: 2, regions: 2 });
  assert.deepEqual(summary.constructs[1], { construct: 'choice', firstErrorFiles: 0, regions: 1 });
  assert.deepEqual(summary.sources.find(s => s.source === 'two'), { source: 'two', files: 1, clean: 1 });
});

test('the report is markdown with the totals and the constructs in order of how many files they stop', () => {
  const summary = summarize([
    { file: 'a.daml', source: 'one', regions: [{ construct: 'choice', line: 3, detail: '' }] },
    { file: 'b.daml', source: 'one', regions: [{ construct: 'template', line: 1, detail: '' }] },
    { file: 'c.daml', source: 'one', regions: [{ construct: 'template', line: 1, detail: '' }] },
  ]);

  const report = renderReport(summary, { generated: '2026-10-14', cli: '0.27.1', corpus: '2026-10-14T09:28:30Z', grammar: 'abc1234' });

  assert.match(report, /^# /);
  assert.match(report, /\| Files \| 3 \|/);
  assert.ok(report.indexOf('| template |') < report.indexOf('| choice |'), 'template stops more files, so it comes first');
});
