import { test } from 'node:test';
import assert from 'node:assert/strict';
import { countConstructs, splitTrees, evaluate, minimumFilesOf } from './corpus-gate.mjs';

const clean = (path, source = 'daml') => ({ path, source, clean: true });
const failing = (path, source = 'daml') => ({ path, source, clean: false });
const everyConstruct = { template: 1, choice: 1, interface: 1, 'interface instance': 1, exception: 1, 'contract key': 1, 'try … catch': 1, record: 1 };

test('the trees of a parse run are split in input order, an error tree included', () => {
  const output = [
    '(daml [0, 0] - [2, 0]',
    '  (template_declaration [0, 0] - [1, 0]))',
    '(ERROR [0, 0] - [1, 0]',
    '  (variable [0, 0] - [0, 1]))',
    'b.daml\tParse: 0.1 ms\t(ERROR [0, 0] - [1, 0])',
    '(daml [0, 0] - [1, 0])',
  ].join('\n');

  const trees = splitTrees(output);

  assert.equal(trees.length, 3);
  assert.match(trees[0], /template_declaration/);
  assert.match(trees[1], /^\(ERROR/);
});

test('constructs are counted by their nodes in a tree', () => {
  const tree = [
    '(daml (declarations',
    '  (template_declaration name: (name)',
    '    body: (template_body (key_clause) (choice_declaration) (choice_declaration)',
    '      (interface_instance)))',
    '  (bind match: (match expression: (try body: (do) alternatives: (alternatives))))',
    '  (bind match: (match expression: (record expression: (constructor))))))',
  ].join('\n');

  const counts = countConstructs(tree);

  assert.equal(counts.template, 1);
  assert.equal(counts.choice, 2);
  assert.equal(counts['interface instance'], 1);
  assert.equal(counts.interface, 0, 'interface_instance is not an interface declaration');
  assert.equal(counts['contract key'], 1);
  assert.equal(counts['try … catch'], 1);
  assert.equal(counts.record, 1);
});

test('a corpus with every file clean and every construct passes', () => {
  const verdict = evaluate({ minimumFiles: 2, results: [clean('a.daml'), clean('b.daml')], exceptions: [], counts: everyConstruct });

  assert.equal(verdict.passed, true);
  assert.deepEqual(verdict.problems, []);
});

test('an empty corpus fails, so the gate cannot pass without one', () => {
  const verdict = evaluate({ minimumFiles: 500, results: [], exceptions: [], counts: everyConstruct });

  assert.equal(verdict.passed, false);
  assert.match(verdict.problems.join('\n'), /empty/);
});

test('a corpus smaller than the pinned minimum fails', () => {
  const verdict = evaluate({ minimumFiles: 3, results: [clean('a.daml'), clean('b.daml')], exceptions: [], counts: everyConstruct });

  assert.equal(verdict.passed, false);
  assert.match(verdict.problems.join('\n'), /2 files.*at least 3/);
});

test('a failing file passes only as a known exception with its reason', () => {
  const results = [...Array.from({ length: 199 }, (_, i) => clean(`c${i}.daml`)), failing('broken.daml')];

  assert.equal(evaluate({ minimumFiles: 1, results, exceptions: [], counts: everyConstruct }).passed, false);
  const known = evaluate({ minimumFiles: 1, results, exceptions: [{ path: 'broken.daml', reason: 'deliberately broken' }], counts: everyConstruct });
  assert.equal(known.passed, true);
});

test('more than 1% of files failing fails, even when every one is a known exception', () => {
  const results = [...Array.from({ length: 98 }, (_, i) => clean(`c${i}.daml`)), failing('x.daml'), failing('y.daml')];
  const exceptions = [{ path: 'x.daml', reason: 'r' }, { path: 'y.daml', reason: 'r' }];

  const verdict = evaluate({ minimumFiles: 1, results, exceptions, counts: everyConstruct });

  assert.equal(verdict.passed, false);
  assert.match(verdict.problems.join('\n'), /98\.0%/);
});

test('an exception for a file that now parses, or is gone, fails, so the list stays true', () => {
  const verdict = evaluate({ minimumFiles: 1, results: [clean('fixed.daml')], exceptions: [{ path: 'fixed.daml', reason: 'r' }, { path: 'gone.daml', reason: 'r' }], counts: everyConstruct });

  assert.equal(verdict.passed, false);
  assert.match(verdict.problems.join('\n'), /fixed\.daml/);
  assert.match(verdict.problems.join('\n'), /gone\.daml/);
});

test('a construct the corpus never shows fails', () => {
  const verdict = evaluate({ minimumFiles: 1, results: [clean('a.daml')], exceptions: [], counts: { ...everyConstruct, exception: 0 } });

  assert.equal(verdict.passed, false);
  assert.match(verdict.problems.join('\n'), /exception/);
});

test('the pinned minimum is read from the corpus sources', () => {
  assert.equal(minimumFilesOf("@{\n    MinimumFiles = 500\n    Sources = @()\n}"), 500);
  assert.equal(minimumFilesOf('@{ Sources = @() }'), undefined);
});
