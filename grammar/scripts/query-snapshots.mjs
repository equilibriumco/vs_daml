// Snapshot tests for the queries `tree-sitter test` can't check itself (folds, indents): each
// fixture under tree-sitter-daml/test/queries/<query>/ is run through `tree-sitter query` with
// queries/<query>.scm, and its captures are compared with the reviewed .expected file next to it.
//
//   node grammar/scripts/query-snapshots.mjs --update    rewrites the .expected files; review the diff
//
// The parser must have been generated (`tree-sitter generate`), as for `tree-sitter test`.
import { spawnSync } from 'node:child_process';
import { readdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const grammarDir = join(here, '..', 'tree-sitter-daml');
const fixturesDir = join(grammarDir, 'test', 'queries');
const cli = join(grammarDir, 'node_modules', 'tree-sitter-cli', 'cli.js');

const captureLine = /capture: (?:\d+ - )?([\w.]+), start: \((\d+), (\d+)\), end: \((\d+), (\d+)\)/;

/** The captures in `tree-sitter query` output, as name, start and end (0-based row and column). */
export function parseCaptures(output) {
  const captures = [];
  for (const line of output.split(/\r?\n/)) {
    const m = captureLine.exec(line);
    if (m) {
      captures.push({
        name: m[1],
        start: { row: Number(m[2]), column: Number(m[3]) },
        end: { row: Number(m[4]), column: Number(m[5]) },
      });
    }
  }
  return captures;
}

/**
 * One line per capture, in source order: `@name startLine:startColumn-endLine:endColumn` (1-based,
 * as editors show them) and the source line the capture starts on, from its start, so a reviewer
 * sees what was captured.
 */
export function formatCaptures(captures, source) {
  const lines = source.replace(/\r\n/g, '\n').split('\n');
  const sorted = [...captures].sort((a, b) =>
    a.start.row - b.start.row || a.start.column - b.start.column ||
    b.end.row - a.end.row || b.end.column - a.end.column || a.name.localeCompare(b.name));
  const out = sorted.map(c => {
    const text = (lines[c.start.row] ?? '').slice(c.start.column).trimEnd();
    return `@${c.name} ${c.start.row + 1}:${c.start.column + 1}-${c.end.row + 1}:${c.end.column + 1} ${text}`;
  });
  return out.join('\n') + '\n';
}

/** The fixtures: query name, fixture name, source file and snapshot file. */
export function fixtures() {
  if (!existsSync(fixturesDir)) return [];
  const result = [];
  for (const query of readdirSync(fixturesDir, { withFileTypes: true }).filter(d => d.isDirectory()).map(d => d.name).sort()) {
    for (const file of readdirSync(join(fixturesDir, query)).filter(f => f.endsWith('.daml')).sort()) {
      const source = join(fixturesDir, query, file);
      result.push({ query, name: basename(file, '.daml'), source, expected: source.replace(/\.daml$/, '.expected') });
    }
  }
  return result;
}

/** Runs queries/<query>.scm over `source` with the pinned CLI and returns its captures. */
export function runQuery(query, source) {
  const run = spawnSync(process.execPath, [cli, 'query', join(grammarDir, 'queries', `${query}.scm`), source], {
    cwd: grammarDir,
    encoding: 'utf8',
  });
  if (run.status !== 0) {
    throw new Error(`tree-sitter query ${query} failed (exit ${run.status}): ${run.stderr}`);
  }
  return parseCaptures(run.stdout);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1] && process.argv.includes('--update')) {
  for (const fixture of fixtures()) {
    writeFileSync(fixture.expected, formatCaptures(runQuery(fixture.query, fixture.source), readFileSync(fixture.source, 'utf8')));
    console.log(`wrote ${fixture.expected}`);
  }
}
