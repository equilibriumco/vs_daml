import { test } from 'node:test';
import assert from 'node:assert/strict';
import { testInputs, seedName } from './fuzz-seeds.mjs';

test('the inputs of a corpus test file are read between its headers and its separator', () => {
  const file = [
    '================================================================================',
    'record: first',
    '================================================================================',
    '',
    'a = T with x = 1',
    '',
    '--------------------------------------------------------------------------------',
    '',
    '(daml)',
    '',
    '================================================================================',
    'record: second',
    '================================================================================',
    '',
    'b = 2',
    'c = 3',
    '',
    '---',
    '',
    '(daml)',
    '',
  ].join('\n');

  assert.deepEqual(testInputs(file), ['a = T with x = 1\n', 'b = 2\nc = 3\n']);
});

test('a file with Windows line endings gives the same inputs', () => {
  const file = '=====\nname\n=====\n\nx = 1\n\n-----\n\n(daml)\n'.replace(/\n/g, '\r\n');

  assert.deepEqual(testInputs(file), ['x = 1\n']);
});

test('a seed is named by its content, so equal seeds are one file', () => {
  assert.equal(seedName('x = 1\n'), seedName('x = 1\n'));
  assert.notEqual(seedName('x = 1\n'), seedName('x = 2\n'));
  assert.match(seedName('x'), /^[0-9a-f]{16}\.daml$/);
});
