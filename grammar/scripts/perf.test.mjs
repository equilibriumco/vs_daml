import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseTimings, median, percentile, editAt, verdicts } from './perf.mjs';

test('the parse and edit times are read from what the CLI prints with --time', () => {
  const output = [
    '../../test/fixtures/perf/large-5k.daml\tParse:   35.11 ms\t  4084 bytes/ms',
    '                                      \tEdit:    19.65 ms',
    '                                      \tEdit:     0.42 ms',
  ].join('\n');

  assert.deepEqual(parseTimings(output), { parseMs: 35.11, editMs: [19.65, 0.42] });
});

test('a run that printed no time is an error, not a fast run', () => {
  assert.throws(() => parseTimings('Error: could not load the parser'), /no parse time/);
});

test('the median is the middle value, or the mean of the two middle ones', () => {
  assert.equal(median([3, 1, 2]), 2);
  assert.equal(median([4, 1, 3, 2]), 2.5);
});

test('the 95th percentile is the value at or above 95% of the samples', () => {
  const values = Array.from({ length: 20 }, (_, i) => i + 1);

  assert.equal(percentile(values, 95), 19);
  assert.equal(percentile([5], 95), 5);
});

test('an edit is found by the text it is made at', () => {
  const text = 'f x = do\n  assertMsg "self" (newOwner /= owner)\n  pure ()\n';

  const edit = editAt(text, { at: 'newOwner /= owner', offset: 8, delete: 0, insert: 'x' });

  assert.equal(edit, '1,28 0 x');
  assert.throws(() => editAt(text, { at: 'missing', offset: 0, delete: 0, insert: '' }), /not in the fixture/);
});

test('a median over its budget fails; the 95th percentile is only reported', () => {
  const results = verdicts({
    parse: { median: 18, p95: 40 },
    edits: [{ name: 'letter', median: 1.5, p95: 9 }, { name: 'line', median: 2.5, p95: 3 }],
  }, { parseMs: 20, editMs: 2 });

  assert.deepEqual(results.map(r => [r.name, r.passed]), [['full parse', true], ['letter', true], ['line', false]]);
});
