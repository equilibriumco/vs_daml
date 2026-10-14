import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { parseCaptures, formatCaptures, fixtures, runQuery } from './query-snapshots.mjs';

test('captures are read from both shapes the CLI prints', () => {
  const output = [
    'C:/work/Main.daml',
    '  pattern: 0',
    '    capture: fold, start: (2, 0), end: (5, 0)',
    '  pattern: 1',
    '    capture: 0 - fold, start: (6, 6), end: (6, 11), text: `y = x`',
  ].join('\n');

  assert.deepEqual(parseCaptures(output), [
    { name: 'fold', start: { row: 2, column: 0 }, end: { row: 5, column: 0 } },
    { name: 'fold', start: { row: 6, column: 6 }, end: { row: 6, column: 11 } },
  ]);
});

test('a snapshot line names the capture, its range and the text it starts with, in source order', () => {
  const source = 'module Main where\n\nimport DA.List\nimport DA.Map\n';
  const captures = [
    { name: 'fold', start: { row: 3, column: 0 }, end: { row: 3, column: 13 } },
    { name: 'fold', start: { row: 2, column: 0 }, end: { row: 4, column: 0 } },
  ];

  assert.equal(formatCaptures(captures, source), [
    '@fold 3:1-5:1 import DA.List',
    '@fold 4:1-4:14 import DA.Map',
    '',
  ].join('\n'));
});

for (const fixture of fixtures()) {
  test(`${fixture.query}: ${fixture.name} matches its snapshot`, () => {
    assert.ok(existsSync(fixture.expected), `no snapshot; run node grammar/scripts/query-snapshots.mjs --update and review it`);
    const actual = formatCaptures(runQuery(fixture.query, fixture.source), readFileSync(fixture.source, 'utf8'));
    assert.equal(actual, readFileSync(fixture.expected, 'utf8').replace(/\r\n/g, '\n'));
  });
}

test('every query without a CLI test of its own has fixtures', () => {
  const queries = new Set(fixtures().map(f => f.query));
  assert.ok(queries.has('folds'), 'folds');
  assert.ok(queries.has('indents'), 'indents');
});
