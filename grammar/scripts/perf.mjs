// Parsing speed against the budgets: a full parse of a 5 000-line Daml file under 40 ms, and an
// incremental reparse after a typical edit under 25 ms (medians). That is what the grammar it
// inherits from tree-sitter-haskell can do: nodes built while the parser weighs several readings
// can't be reused, so an edit parses much of the file again; the budgets guard against it getting
// worse. An editor parses in the background after typing pauses. The file is the one the lexer
// benchmarks use (test/fixtures/perf/large-5k.daml). Each run is a fresh `tree-sitter parse
// --time` process: its "Parse" time is the full parse, and with `--edits` its "Edit" time covers
// applying the edit to the tree and the text and parsing again incrementally.
//
//   node grammar/scripts/perf.mjs [--runs N]    exit code 1 when a median is over its budget
//
// Timings need a quiet machine; on a shared CI runner they are only a rough guide.
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { toCliEdit } from './edit-properties.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, '..', '..');
const packageDir = join(here, '..', 'tree-sitter-daml');
const binary = join(packageDir, 'node_modules', 'tree-sitter-cli', process.platform === 'win32' ? 'tree-sitter.exe' : 'tree-sitter');
export const fixture = join(repoRoot, 'test', 'fixtures', 'perf', 'large-5k.daml');

export const budgets = { parseMs: 40, editMs: 25 };

// Edits a user makes, found by the text they are made at (the first occurrence in the fixture).
export const typicalEdits = [
  { name: 'a letter typed in a choice body', at: 'newOwner /= owner', offset: 8, delete: 0, insert: 'x' },
  { name: 'a new statement in a do block', at: 'assertMsg "cannot transfer to self" (newOwner /= owner)', offset: 55, delete: 0, insert: '\n        pure ()' },
  { name: 'a line deleted in a do block', at: '        let rest = amount - splitAmount\n', offset: 0, delete: 40, insert: '' },
  { name: 'a letter typed in a function', at: 'summarise1 positions =', offset: 20, delete: 0, insert: 'x' },
  { name: 'a field added to a template', at: '    label : Text\n', offset: 16, delete: 0, insert: '\n    note : Text' },
];

/** The times `tree-sitter parse --time` printed: the full parse and each edit, in ms. */
export function parseTimings(output) {
  const parse = /Parse:\s+([\d.]+) ms/.exec(output);
  if (!parse) {
    throw new Error(`no parse time in the CLI's output: ${output.slice(0, 200)}`);
  }
  return { parseMs: Number(parse[1]), editMs: [...output.matchAll(/Edit:\s+([\d.]+) ms/g)].map(m => Number(m[1])) };
}

export function median(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
}

/** The value at or above `p` percent of the samples (nearest rank). */
export function percentile(values, p) {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.max(0, Math.ceil((p / 100) * sorted.length) - 1)];
}

/** The CLI edit for `edit`, placed at the first occurrence of its `at` text. */
export function editAt(text, edit) {
  const index = text.indexOf(edit.at);
  if (index < 0) {
    throw new Error(`"${edit.at}" is not in the fixture`);
  }
  return toCliEdit(text, { position: index + edit.offset, delete: edit.delete, insert: edit.insert });
}

/** Medians against the budgets; the 95th percentiles are reported, not judged. */
export function verdicts({ parse, edits }, limits) {
  return [
    { name: 'full parse', median: parse.median, p95: parse.p95, budget: limits.parseMs, passed: parse.median <= limits.parseMs },
    ...edits.map(e => ({ name: e.name, median: e.median, p95: e.p95, budget: limits.editMs, passed: e.median <= limits.editMs })),
  ];
}

function timed(args) {
  const run = spawnSync(binary, ['parse', fixture, '--quiet', '--time', ...args], { cwd: packageDir, encoding: 'utf8' });
  if (run.error) {
    throw run.error;
  }
  return parseTimings(run.stdout);
}

function main() {
  const runsIndex = process.argv.indexOf('--runs');
  const runs = runsIndex > 0 ? Number(process.argv[runsIndex + 1]) : 15;
  const text = readFileSync(fixture, 'utf8');
  const parses = [];
  const edits = typicalEdits.map(edit => ({ name: edit.name, cli: editAt(text, edit), times: [] }));
  timed([]); // the first run loads the parser library from disk; it isn't counted
  for (let i = 0; i < runs; i++) {
    for (const edit of edits) {
      const t = timed(['--edits', edit.cli]);
      parses.push(t.parseMs);
      edit.times.push(t.editMs[0]);
    }
  }
  const results = verdicts({
    parse: { median: median(parses), p95: percentile(parses, 95) },
    edits: edits.map(e => ({ name: e.name, median: median(e.times), p95: percentile(e.times, 95) })),
  }, budgets);
  const lines = text.split('\n').length;
  console.log(`${lines} lines, ${runs} runs of each edit (${parses.length} full parses):`);
  for (const r of results) {
    console.log(`  ${r.passed ? 'ok  ' : 'OVER'} ${r.name.padEnd(34)} median ${r.median.toFixed(2).padStart(7)} ms  p95 ${r.p95.toFixed(2).padStart(7)} ms  budget ${r.budget} ms`);
  }
  process.exitCode = results.every(r => r.passed) ? 0 : 1;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  main();
}
