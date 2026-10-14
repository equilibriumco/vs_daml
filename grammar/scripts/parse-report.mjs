#!/usr/bin/env node
// Parses the real-world corpus (grammar/test/real-world, collected by scripts/collect-corpus.ps1)
// with the grammar and writes grammar/docs/baseline.md: how many files parse cleanly, and for
// the rest, which Daml construct each error sits in. Errors nested in a larger error are
// consequences of it and aren't counted; the first error of a file is what stops it.
//
//   node grammar/scripts/parse-report.mjs [--out <file>]
//
// The constructs are found from the source text, not the tree: while the grammar doesn't know a
// construct, its tree has nothing to show for it.
import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const grammarRoot = resolve(here, '..');
const packageDir = join(grammarRoot, 'tree-sitter-daml');
const corpusDir = join(grammarRoot, 'test', 'real-world');

// A file the parser couldn't recover at all has an ERROR for its root.
const treeStart = /^\((daml|ERROR) \[/;
const nodeLine = /\((ERROR|MISSING)\b[^[]*\[(\d+), (\d+)\] - \[(\d+), (\d+)\]/g;
const summaryLine = /^(.*?)\s+Parse:\s+[\d.]+ ms/;

/**
 * The trees the CLI printed, in the order of the files it was given, each with its ERROR and
 * MISSING nodes. Every file gets a tree; only a file with errors also gets a summary line naming
 * it, which is kept as `file` to check the order by.
 */
export function parseCliOutput(text) {
  const trees = [];
  let current = null;
  for (const line of text.split(/\r?\n/)) {
    if (treeStart.test(line)) {
      current = { file: undefined, errors: [] };
      trees.push(current);
    }
    const summary = summaryLine.exec(line);
    if (summary) {
      if (current) {
        current.file = summary[1].trim();
      }
      continue;
    }
    if (!current) {
      continue;
    }
    for (const m of line.matchAll(nodeLine)) {
      current.errors.push({
        kind: m[1],
        start: { row: Number(m[2]), column: Number(m[3]) },
        end: { row: Number(m[4]), column: Number(m[5]) },
      });
    }
  }
  return trees;
}

const before = (a, b) => a.row < b.row || (a.row === b.row && a.column <= b.column);
const contains = (outer, inner) => before(outer.start, inner.start) && before(inner.end, outer.end);

const same = (a, b) => a.start.row === b.start.row && a.start.column === b.start.column
  && a.end.row === b.end.row && a.end.column === b.end.column;

/**
 * The errors not inside another error, in source order. When the parser gives up on a stretch
 * of code it wraps all of it, from the last point it understood, in one error; where it actually
 * went wrong is the first innermost error inside, kept as the region's `anchor`.
 */
export function outermostRegions(errors) {
  const sorted = [...errors].sort((a, b) => a.start.row - b.start.row || a.start.column - b.start.column
    || b.end.row - a.end.row || b.end.column - a.end.column);
  const regions = [];
  for (const error of sorted) {
    if (!regions.some(region => region.kind === 'ERROR' && contains(region, error))) {
      regions.push(error);
    }
  }
  return regions.map(region => {
    const inner = sorted.filter(e => e !== region && !same(e, region) && contains(region, e));
    const leaves = inner.filter(e => !inner.some(other => other !== e && !same(other, e) && e.kind === 'ERROR' && contains(e, other)));
    const anchor = leaves.length > 0 ? leaves[0].start : region.start;
    return { ...region, anchor };
  });
}

const indentOf = line => line.length - line.trimStart().length;
const isBlank = line => line.trim() === '' || /^\s*--/.test(line);
// Lines that only open a block belong to whatever opened it.
const layoutOnly = /^\s*(with|where|do|of|let|in)\s*(--.*)?$/;

const signature = 'type signature with `:`';
const endsWithWith = /\bwith\s*(--.*)?$/;

// Innermost first: the first rule matching a line of the error's chain names the construct. A
// rule sees the line and the line enclosing it.
const rules = [
  [/^\s*(nonconsuming|preconsuming|postconsuming)\s+choice\b|^\s*choice\b/, 'choice'],
  [/^\s*controller\b.*\bcan\b/, 'controller … can (Daml 1.x)'],
  [/^\s*controller\b/, 'choice'],
  [/^\s*interface\s+instance\b/, 'interface instance'],
  [/^\s*(interface|viewtype|requires)\b/, 'interface'],
  [/^\s*template\b/, 'template'],
  [/^\s*(signatory|observer|ensure|agreement)\b/, text => `template clause: ${text.trim().split(/\s+/)[0]}`],
  [/^\s*(key|maintainer)\b/, 'contract key'],
  [/^\s*(exception|message)\b/, 'exception'],
  [/^\s*data\b.*=\s*[A-Z][\w']*\s+with\s*(--.*)?$/, 'record with declaration'],
  // Daml writes `name : Type` where Haskell writes `name :: Type`. Under a `with` it is a field of
  // the declaration that opened the block, and that declaration names the construct.
  [/^\s*pattern\s+[A-Z][\w']*\s*:(?!:)/, signature],
  [(text, parent) => /^\s*[a-z_][\w']*(\s*,\s*[a-z_][\w']*)*\s*:(?!:)/.test(text) && !(parent !== undefined && endsWithWith.test(parent)), signature],
  // The rest of a signature whose name stands alone on the line above.
  [/^\s*:(?!:)/, signature],
  // Record fields in braces and kind annotations in parentheses.
  [/[{,(]\s*[a-z_][\w']*\s*:(?!:)/, signature],
  [/\S\s+with\b/, 'record with expression'],
];

// Declarations whose `with` block holds their fields; any other `with` builds or updates a record.
const declaresFields = /^\s*((nonconsuming|preconsuming|postconsuming)\s+)?(template|choice|exception|data|interface\s+instance)\b|^\s*[A-Z][\w']*\s*:/;
const bareWith = /^\s*with\s*(--.*)?$/;

const matches = (rule, text, parent) => (typeof rule === 'function' ? rule(text, parent) : rule.test(text));

// The error's line and each enclosing (less indented) line, outwards, as line numbers.
function chain(lines, row) {
  const found = [];
  let indent = Infinity;
  for (let line = row; line >= 0; line--) {
    const text = lines[line];
    if (isBlank(text) || indentOf(text) >= indent) {
      continue;
    }
    found.push(line);
    indent = indentOf(text);
    if (indent === 0) {
      break;
    }
  }
  return found;
}

/**
 * The Daml construct the error on `row` is in: the line itself and then each enclosing line,
 * outwards, until one of them says. Lines that only open a block are passed over.
 */
export function classify(lines, row) {
  // `-> T` and `=> T` lines continue a signature started on some line above, often at the same
  // indent; walk back through the signature to its start.
  if (/^\s*(->|=>)/.test(lines[row] ?? '')) {
    for (let line = row - 1; line >= 0 && !isBlank(lines[line]); line--) {
      const text = lines[line];
      if (/^\s*:(?!:)/.test(text) || /^\s*[a-z_][\w']*(\s*,\s*[a-z_][\w']*)*\s*:(?!:)/.test(text) || /^\s*pattern\s+[A-Z][\w']*\s*:(?!:)/.test(text)) {
        return { construct: signature, line: line + 1, detail: '' };
      }
      if (indentOf(text) === 0) {
        break;
      }
    }
  }
  const enclosing = chain(lines, row);
  for (let k = 0; k < enclosing.length; k++) {
    const text = lines[enclosing[k]];
    // The enclosing line as written, a bare `with` included, so a field knows it is one.
    const parent = k + 1 < enclosing.length ? lines[enclosing[k + 1]] : undefined;
    if (bareWith.test(text) && parent !== undefined && !declaresFields.test(parent)) {
      return { construct: 'record with expression', line: enclosing[k] + 1, detail: '' };
    }
    if (layoutOnly.test(text)) {
      continue;
    }
    for (const [rule, name] of rules) {
      if (matches(rule, text, parent)) {
        return { construct: typeof name === 'function' ? name(text) : name, line: enclosing[k] + 1, detail: '' };
      }
    }
  }
  const first = (lines[row] ?? '').trim().split(/\s+/)[0] ?? '';
  return { construct: 'other', line: row + 1, detail: first };
}

/** Totals, constructs ordered by how many files they stop first, and clean files per source. */
export function summarize(results) {
  const constructs = new Map();
  const entry = name => constructs.get(name) ?? constructs.set(name, { construct: name, firstErrorFiles: 0, regions: 0 }).get(name);
  const sources = new Map();
  const others = new Map();
  let clean = 0;
  let regions = 0;
  for (const result of results) {
    const source = sources.get(result.source) ?? sources.set(result.source, { source: result.source, files: 0, clean: 0 }).get(result.source);
    source.files++;
    if (result.regions.length === 0) {
      clean++;
      source.clean++;
      continue;
    }
    entry(result.regions[0].construct).firstErrorFiles++;
    for (const region of result.regions) {
      entry(region.construct).regions++;
      regions++;
      if (region.construct === 'other') {
        const other = others.get(region.detail) ?? others.set(region.detail, { detail: region.detail, regions: 0, example: `${result.file}:${region.line}` }).get(region.detail);
        other.regions++;
      }
    }
  }
  return {
    totals: { files: results.length, clean, withErrors: results.length - clean, regions },
    constructs: [...constructs.values()].sort((a, b) => b.firstErrorFiles - a.firstErrorFiles || b.regions - a.regions || a.construct.localeCompare(b.construct)),
    sources: [...sources.values()].sort((a, b) => a.source.localeCompare(b.source)),
    others: [...others.values()].sort((a, b) => b.regions - a.regions || a.detail.localeCompare(b.detail)),
  };
}

const tasks = {
  [signature]: 'before M4-T04: Daml\'s `:`/`::`',
  template: 'M4-T04',
  'contract key': 'M4-T07 (lower priority)',
  choice: 'M4-T05',
  'controller … can (Daml 1.x)': 'M4-T07 (only if in the corpus)',
  interface: 'M4-T06',
  'interface instance': 'M4-T06',
  'record with declaration': 'M4-T07',
  'record with expression': 'M4-T07',
  exception: 'M4-T07 (lower priority)',
  other: 'to look at',
};
const taskOf = construct => tasks[construct] ?? (construct.startsWith('template clause') ? 'M4-T04' : 'to look at');
const percent = (part, whole) => (whole === 0 ? '–' : `${((100 * part) / whole).toFixed(1)}%`);

/** The baseline report in markdown. */
export function renderReport(summary, meta) {
  const { totals } = summary;
  const out = [];
  out.push('# Baseline: the real-world corpus parsed by the grammar', '');
  out.push(`Generated ${meta.generated} by \`grammar/scripts/parse-report.mjs\` with tree-sitter CLI ${meta.cli}, grammar at \`${meta.grammar}\`,`);
  out.push(`corpus collected ${meta.corpus} (\`grammar/test/real-world/manifest.json\`).`, '');
  out.push('An error inside a larger error is a consequence of it and isn\'t counted; a file\'s *first* error');
  out.push('is what stops it. A large error is placed at the first innermost error inside it, where the');
  out.push('parse actually went wrong, and named by the nearest Daml declaration or clause around that');
  out.push('place, read from the source (the grammar can\'t show a construct it doesn\'t know yet). Daml\'s');
  out.push('`:` for types, where Haskell has `::`, gets its own row: it is under most of the other constructs.', '');
  out.push('| | |', '|---|---|');
  out.push(`| Files | ${totals.files} |`);
  out.push(`| Parse without errors | ${totals.clean} (${percent(totals.clean, totals.files)}) |`);
  out.push(`| With errors | ${totals.withErrors} |`);
  out.push(`| Error regions | ${totals.regions} |`, '');
  out.push('## By construct', '');
  out.push('| Construct | Files it stops first | Error regions | Task |', '|---|---:|---:|---|');
  for (const c of summary.constructs) {
    out.push(`| ${c.construct} | ${c.firstErrorFiles} | ${c.regions} | ${taskOf(c.construct)} |`);
  }
  out.push('', '## By source', '');
  out.push('| Source | Files | Without errors |', '|---|---:|---:|');
  for (const s of summary.sources) {
    out.push(`| ${s.source} | ${s.files} | ${s.clean} (${percent(s.clean, s.files)}) |`);
  }
  if (summary.others.length > 0) {
    out.push('', '## Errors outside the Daml constructs', '');
    out.push('By the first word of the line the error is on, with one example each.', '');
    out.push('| Line starts with | Error regions | Example |', '|---|---:|---|');
    for (const o of summary.others.slice(0, 25)) {
      out.push(`| \`${o.detail.replace(/\|/g, '\\|') || '(blank)'}\` | ${o.regions} | ${o.example} |`);
    }
  }
  return out.join('\n') + '\n';
}

function cli(args, options = {}) {
  const result = spawnSync(process.execPath, [join(packageDir, 'node_modules', 'tree-sitter-cli', 'cli.js'), ...args],
    { cwd: packageDir, encoding: 'utf8', maxBuffer: 1024 * 1024 * 1024, ...options });
  if (result.error) {
    throw result.error;
  }
  return result;
}

function main() {
  const outIndex = process.argv.indexOf('--out');
  const out = outIndex > 0 ? resolve(process.argv[outIndex + 1]) : join(grammarRoot, 'docs', 'baseline.md');
  const manifest = JSON.parse(readFileSync(join(corpusDir, 'manifest.json'), 'utf8'));
  const entries = manifest.entries.map(e => ({ ...e, full: join(corpusDir, e.path) }));

  const work = mkdtempSync(join(tmpdir(), 'daml-baseline-'));
  try {
    const list = join(work, 'files.txt');
    writeFileSync(list, entries.map(e => e.full).join('\n'));
    // A failed parse makes the CLI exit with 1; its output is what we want either way.
    const parsed = cli(['parse', '--paths', list]);
    const trees = parseCliOutput(parsed.stdout);
    if (trees.length !== entries.length) {
      throw new Error(`The CLI printed ${trees.length} trees for ${entries.length} files.\n${parsed.stderr}`);
    }
    const results = trees.map(({ file, errors }, index) => {
      const entry = entries[index];
      if (file !== undefined && resolve(packageDir, file).toLowerCase() !== resolve(entry.full).toLowerCase()) {
        throw new Error(`Tree ${index} is for ${file}, expected ${entry.full}: the CLI's order isn't the list's.`);
      }
      const lines = readFileSync(entry.full, 'utf8').split(/\r?\n/);
      const regions = outermostRegions(errors).map(region => classify(lines, region.anchor.row));
      return { file: entry.path, source: entry.source, regions };
    });
    const summary = summarize(results);
    const grammar = spawnSync('git', ['rev-parse', '--short', 'HEAD'], { cwd: grammarRoot, encoding: 'utf8' }).stdout.trim() || 'unknown';
    const report = renderReport(summary, {
      generated: new Date().toISOString().slice(0, 10),
      cli: cli(['--version']).stdout.trim().replace(/^tree-sitter\s+/, ''),
      corpus: manifest.generatedAt,
      grammar,
    });
    writeFileSync(out, report);
    console.log(`${summary.totals.clean} of ${summary.totals.files} files parse without errors; report: ${relative(process.cwd(), out)}`);
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  main();
}
