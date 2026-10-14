// Properties of incremental parsing, after random edits to real Daml files (the real-world corpus
// when it has been collected, this repository's samples otherwise): deletions, copied pieces of the
// file, line breaks with indentation and Daml's own words, one to three at a time. `tree-sitter
// parse --edits` applies them to the old tree and parses again.
//
// - When the edited text parses without errors, the incremental tree is the tree a parse from
//   scratch gives, positions included.
// - Undoing the edits, also incrementally, gives back the tree of the original file (when that one
//   parses without errors).
//
// One difference is let through: a node may end elsewhere when only whitespace lies between the
// two ends. The layout scanner inherited from tree-sitter-haskell ends a block before or after the
// line break and indentation that follow it depending on which old subtrees get reused (a `case`
// with a `where` after its alternatives shows it, in unmodified tree-sitter-haskell too). The
// fuzzer harness allows the same; such comparisons are counted.
//
// Where an edit leaves errors, the trees may differ: error recovery depends on which old subtrees
// the parser reuses, and tree-sitter doesn't promise the same recovery both ways (its own fuzzer
// checks only the undo). Those cases are counted, and must still produce a tree.
//
//   node grammar/scripts/edit-properties.mjs [--cases N] [--seed S]    exit code 1 on a mismatch
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, '..', '..');
const packageDir = join(here, '..', 'tree-sitter-daml');
const corpusDir = join(here, '..', 'test', 'real-world');
const binary = join(packageDir, 'node_modules', 'tree-sitter-cli', process.platform === 'win32' ? 'tree-sitter.exe' : 'tree-sitter');

/** A small seeded generator (mulberry32), so a failing case can be run again. */
export function randomGenerator(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6D2B79F5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Applies { position, delete, insert } edits in order, each to the result of the one before. */
export function applyEdits(text, edits) {
  let result = text;
  for (const { position, delete: count, insert } of edits) {
    result = result.slice(0, position) + insert + result.slice(position + count);
  }
  return result;
}

const utf8Length = text => Buffer.byteLength(text, 'utf8');

/** An edit as `tree-sitter parse --edits` takes it: row,column delete insert, in bytes. */
export function toCliEdit(text, { position, delete: count, insert }) {
  const lineStart = text.lastIndexOf('\n', position - 1) + 1;
  const row = text.slice(0, lineStart).split('\n').length - 1;
  const column = utf8Length(text.slice(lineStart, position));
  return `${row},${column} ${utf8Length(text.slice(position, position + count))} ${insert}`;
}

const dafmlWords = ['with', 'where', 'do', '->', '(', ')', ' ', 'choice', 'template', ':', '=', '..', 'controller', ',', 'signatory', 'interface', '"', '--', '{-', '-}'];

// Moves an index off the second half of a surrogate pair, so no character is split.
const onBoundary = (text, i) => (i > 0 && i < text.length && /[\uDC00-\uDFFF]/.test(text[i]) ? i - 1 : i);

/** One to three random edits for `text`, each placed in the text the ones before leave. */
export function randomEdits(text, rand) {
  const pick = n => Math.floor(rand() * n);
  const edits = [];
  let current = text;
  const count = 1 + pick(3);
  for (let i = 0; i < count; i++) {
    const position = onBoundary(current, pick(current.length + 1));
    const kind = pick(4);
    let remove = 0;
    let insert = '';
    if (kind === 0) {
      remove = Math.min(1 + pick(20), current.length - position);
    } else if (kind === 1) {
      const from = onBoundary(current, pick(current.length + 1));
      insert = current.slice(from, onBoundary(current, Math.min(current.length, from + 1 + pick(20))));
    } else if (kind === 2) {
      insert = '\n' + ' '.repeat(pick(9));
    } else {
      remove = Math.min(pick(10), current.length - position);
      insert = dafmlWords[pick(dafmlWords.length)];
    }
    remove = onBoundary(current, position + remove) - position;
    const edit = { position, delete: Math.max(0, remove), insert };
    edits.push(edit);
    current = applyEdits(current, [edit]);
  }
  return edits;
}

/** The Daml files to edit: the real-world corpus when it is there, the samples otherwise. */
export function sourceFiles() {
  const manifest = join(corpusDir, 'manifest.json');
  if (existsSync(manifest)) {
    return JSON.parse(readFileSync(manifest, 'utf8')).entries.map(e => join(corpusDir, e.path));
  }
  const found = [];
  const walk = dir => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.isDirectory() && entry.name !== '.daml') walk(join(dir, entry.name));
      else if (entry.isFile() && entry.name.endsWith('.daml')) found.push(join(dir, entry.name));
    }
  };
  walk(join(repoRoot, 'samples'));
  return found.sort();
}

// A line of a printed tree: indentation, maybe a field name, then a node. Other lines name the
// file or time the edit, and differ between the two runs.
const treeLine = /^\s*([a-z_]+: )?\(/;

// The tree the CLI prints.
function tree(args) {
  const run = spawnSync(binary, ['parse', ...args], { cwd: packageDir, encoding: 'utf8', maxBuffer: 1 << 28 });
  if (run.status === null || run.error) {
    throw new Error(`tree-sitter parse failed: ${run.error ?? run.signal}`);
  }
  return run.stdout.split(/\r?\n/).filter(l => treeLine.test(l)).map(l => l.trimEnd()).join('\n');
}

/** The edit that takes `edit`'s result back to the text it was applied to. */
export function inverseEdit(text, { position, delete: count, insert }) {
  return { position, delete: insert.length, insert: text.slice(position, position + count) };
}

const hasErrors = sexp => /\((ERROR|MISSING)\b/.test(sexp);

// A printed node: what comes before its range (indentation, field, kind), the range as rows and
// byte columns, and the parentheses closing it and its parents.
const nodeLine = /^(.*?)\[(\d+), (\d+)\] - \[(\d+), (\d+)\](.*)$/;
const whitespace = new Set([0x20, 0x09, 0x0a, 0x0d]);

/**
 * Compares two printed trees of `text`, line by line. A node may end at a different place when
 * only whitespace lies between the two ends; everything else must match. Returns whether they are
 * the same and whether that took the allowance, or the first two lines that differ.
 */
export function compareTrees(first, second, text) {
  if (first === second) return { same: true, tolerated: false };
  const a = first.split('\n');
  const b = second.split('\n');
  const bytes = Buffer.from(text, 'utf8');
  const lineStarts = [0];
  bytes.forEach((byte, i) => byte === 0x0a && lineStarts.push(i + 1));
  const offset = (row, column) => (lineStarts[row] ?? bytes.length) + Number(column);
  const blank = (x, y) => {
    for (let i = Math.min(x, y); i < Math.max(x, y); i++) if (!whitespace.has(bytes[i])) return false;
    return true;
  };
  for (let k = 0; k < Math.max(a.length, b.length); k++) {
    if (a[k] === b[k]) continue;
    const p = nodeLine.exec(a[k] ?? '');
    const q = nodeLine.exec(b[k] ?? '');
    const onlyEndsDiffer = p && q && p[1] === q[1] && p[2] === q[2] && p[3] === q[3] && p[6] === q[6];
    if (!onlyEndsDiffer || !blank(offset(p[4], p[5]), offset(q[4], q[5]))) {
      return { same: false, first: a[k], second: b[k] };
    }
  }
  return { same: true, tolerated: true };
}

/**
 * Runs `cases` random cases over `files` from `seed`. Returns the failures, each with what is
 * needed to run it again, how many cases left errors in the edited text (checked for a tree only)
 * and how many were compared exactly, and how many comparisons passed only because node ends may
 * differ by whitespace.
 */
export function checkCases({ files, seed, cases }) {
  const rand = randomGenerator(seed);
  const work = mkdtempSync(join(tmpdir(), 'daml-edits-'));
  const failures = [];
  let exact = 0;
  let withErrors = 0;
  let tolerated = 0;
  const originals = new Map();
  try {
    for (let i = 0; i < cases; i++) {
      const file = files[Math.floor(rand() * files.length)];
      const text = readFileSync(file, 'utf8');
      const edits = randomEdits(text, rand);
      const forward = [];
      const backward = [];
      let current = text;
      for (const edit of edits) {
        forward.push(toCliEdit(current, edit));
        const inverse = inverseEdit(current, edit);
        current = applyEdits(current, [edit]);
        backward.unshift({ edit: inverse, before: current });
      }
      const undo = [];
      for (const { edit, before } of backward) {
        undo.push(toCliEdit(before, edit));
      }
      // The inverses apply to the text each one finds; replay them to check they do.
      if (applyEdits(current, backward.map(b => b.edit)) !== text) {
        throw new Error(`undoing the edits of case ${i} doesn't give the original back`);
      }

      const edited = join(work, 'edited.daml');
      writeFileSync(edited, current);
      const incremental = tree([file, ...forward.flatMap(e => ['--edits', e])]);
      const full = tree([edited]);
      const report = extra => failures.push({ case: i, seed, file, edits: forward, ...extra });
      // Two empty outputs would compare equal; a run that printed no tree is a failure of its own.
      if (!incremental || !full) {
        report({ problem: 'no tree', incremental: Boolean(incremental), full: Boolean(full) });
        continue;
      }
      if (hasErrors(full)) {
        withErrors++;
      } else {
        exact++;
        const result = compareTrees(incremental, full, current);
        if (result.tolerated) tolerated++;
        if (!result.same) report({ problem: 'incremental differs from full', incremental: result.first, full: result.second });
      }

      if (!originals.has(file)) originals.set(file, tree([file]));
      const original = originals.get(file);
      if (!hasErrors(original)) {
        const undone = tree([file, ...forward.flatMap(e => ['--edits', e]), ...undo.flatMap(e => ['--edits', e])]);
        const result = compareTrees(undone, original, text);
        if (result.tolerated) tolerated++;
        if (!result.same) report({ problem: 'undoing the edits differs from the original', undo, undone: result.first, original: result.second });
      }
    }
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
  return { failures, exact, withErrors, tolerated };
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const arg = name => {
    const i = process.argv.indexOf(name);
    return i > 0 ? Number(process.argv[i + 1]) : undefined;
  };
  const files = sourceFiles();
  const seed = arg('--seed') ?? Math.floor(Math.random() * 2 ** 31);
  const cases = arg('--cases') ?? 200;
  const { failures, exact, withErrors, tolerated } = checkCases({ files, seed, cases });
  console.log(`${cases} cases over ${files.length} files, seed ${seed}: ${exact} compared exactly, ${withErrors} left errors, ` +
    `${tolerated} comparison(s) with node ends apart by whitespace only; ${failures.length} failure(s)`);
  for (const f of failures) console.error(JSON.stringify(f));
  process.exitCode = failures.length === 0 ? 0 : 1;
}
