// The corpus gate: the real-world corpus (grammar/test/real-world, collected by
// scripts/collect-corpus.ps1) must be there and at least as big as its pinned minimum, at least
// 99% of its files must parse without errors, every file that doesn't must be a known exception
// with its reason (grammar/corpus-exceptions.json), and every Daml construct must occur. Writes
// grammar/docs/corpus.md: the sources, what they contain, the result and the exceptions.
//
//   node grammar/scripts/corpus-gate.mjs            check, write the report; exit code 1 on failure
//   node grammar/scripts/corpus-gate.mjs --out f    write the report elsewhere
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseCliOutput } from './parse-report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const grammarRoot = join(here, '..');
const packageDir = join(grammarRoot, 'tree-sitter-daml');
const corpusDir = join(grammarRoot, 'test', 'real-world');

export const requiredCleanRate = 0.99;

// Daml constructs and the nodes that show them, in the order the report lists them.
export const constructs = [
  ['template', 'template_declaration'],
  ['choice', 'choice_declaration'],
  ['interface', 'interface_declaration'],
  ['interface instance', 'interface_instance'],
  ['exception', 'exception_declaration'],
  ['contract key', 'key_clause'],
  ['try … catch', 'try'],
  ['record', 'record'],
];

const treeStart = /^\((daml|ERROR) \[/;

/** The trees of a `tree-sitter parse` run, as text, in input order. */
export function splitTrees(output) {
  const trees = [];
  let current = null;
  for (const line of output.split(/\r?\n/)) {
    if (treeStart.test(line)) {
      current = [];
      trees.push(current);
    }
    if (current && line.startsWith('(') || current && line.startsWith(' ')) {
      current.push(line);
    }
  }
  return trees.map(lines => lines.join('\n'));
}

/** How many times each construct's node occurs in a tree. */
export function countConstructs(tree) {
  const counts = {};
  for (const [name, node] of constructs) {
    counts[name] = (tree.match(new RegExp(`\\(${node}[\\s)]`, 'g')) ?? []).length;
  }
  return counts;
}

/** The pinned minimum number of files, from grammar/corpus-sources.psd1. */
export function minimumFilesOf(psd1) {
  const m = /MinimumFiles\s*=\s*(\d+)/.exec(psd1);
  return m ? Number(m[1]) : undefined;
}

/**
 * The verdict on a parsed corpus: results are { path, source, clean } per file, exceptions
 * { path, reason }, counts the construct totals. Every reason the gate fails is a problem.
 */
export function evaluate({ minimumFiles, results, exceptions, counts }) {
  const problems = [];
  const files = results.length;
  if (files === 0) {
    problems.push('The corpus is empty; collect it with scripts/collect-corpus.ps1.');
  } else if (minimumFiles !== undefined && files < minimumFiles) {
    problems.push(`The corpus has ${files} files; the sources pin at least ${minimumFiles}.`);
  }

  const failing = results.filter(r => !r.clean).map(r => r.path);
  const known = new Map(exceptions.map(e => [e.path, e.reason]));
  for (const path of failing.filter(p => !known.has(p))) {
    problems.push(`${path} doesn't parse and isn't a known exception.`);
  }
  const failingSet = new Set(failing);
  const inCorpus = new Set(results.map(r => r.path));
  for (const { path } of exceptions) {
    if (!inCorpus.has(path)) {
      problems.push(`The exception for ${path} names a file that isn't in the corpus.`);
    } else if (!failingSet.has(path)) {
      problems.push(`${path} parses now; drop its exception.`);
    }
  }

  const cleanRate = files === 0 ? 0 : (files - failing.length) / files;
  if (files > 0 && cleanRate < requiredCleanRate) {
    problems.push(`${(cleanRate * 100).toFixed(1)}% of the files parse without errors; at least ${requiredCleanRate * 100}% are required.`);
  }

  for (const [name] of constructs) {
    if (!counts[name]) {
      problems.push(`No ${name} occurs in the corpus, so nothing shows that it parses.`);
    }
  }
  return { passed: problems.length === 0, problems, cleanRate, files, failing: failing.length };
}

function renderReport({ meta, verdict, sources, perSource, exceptions }) {
  const percent = rate => `${(rate * 100).toFixed(1)}%`;
  const lines = [
    '# The real-world corpus',
    '',
    `Checked ${meta.date} by \`grammar/scripts/corpus-gate.mjs\` with tree-sitter CLI ${meta.cli}, grammar at \`${meta.grammar}\`,`,
    `corpus collected ${meta.collected} (\`grammar/test/real-world/manifest.json\`).`,
    '',
    `**Gate: ${verdict.passed ? 'passed' : 'failed'}.** ${verdict.files - verdict.failing} of ${verdict.files} files (${percent(verdict.cleanRate)}) parse without errors;`,
    `at least ${requiredCleanRate * 100}% are required, and every other file must be a known exception below.`,
    '',
  ];
  if (!verdict.passed) {
    lines.push('Problems:', '', ...verdict.problems.map(p => `- ${p}`), '');
  }
  lines.push('## Sources', '', '| Source | Pinned at | SDK | License | Files | Without errors |', '|---|---|---|---|---:|---:|');
  for (const s of sources) {
    const stats = perSource.get(s.name) ?? { files: 0, clean: 0 };
    const pin = s.commit ? `${s.ref} \`${s.commit.slice(0, 7)}\`` : 'working tree';
    lines.push(`| ${s.name} | ${pin} | ${s.sdk} | ${s.license} | ${stats.files} | ${stats.clean} |`);
  }
  lines.push('', '## Daml constructs', '', 'How often each construct occurs, counted by its nodes in the parse trees.', '');
  lines.push(`| Construct | ${sources.map(s => s.name).join(' | ')} | Total |`, `|---|${sources.map(() => '---:').join('|')}|---:|`);
  for (const [name] of constructs) {
    const row = sources.map(s => perSource.get(s.name)?.counts[name] ?? 0);
    lines.push(`| ${name} | ${row.join(' | ')} | ${row.reduce((a, b) => a + b, 0)} |`);
  }
  lines.push('', '## Known exceptions', '', 'Files that don\'t parse, and why (`grammar/corpus-exceptions.json`).', '', '| File | Why |', '|---|---|');
  for (const e of exceptions) {
    lines.push(`| \`${e.path}\` | ${e.reason} |`);
  }
  return lines.join('\n') + '\n';
}

function run(args, cwd) {
  return spawnSync(process.execPath, [join(packageDir, 'node_modules', 'tree-sitter-cli', 'cli.js'), ...args],
    { cwd, encoding: 'utf8', maxBuffer: 1 << 30 });
}

function main() {
  const outIndex = process.argv.indexOf('--out');
  const out = outIndex > 0 ? resolve(process.argv[outIndex + 1]) : join(grammarRoot, 'docs', 'corpus.md');
  const manifestPath = join(corpusDir, 'manifest.json');
  const manifest = existsSync(manifestPath) ? JSON.parse(readFileSync(manifestPath, 'utf8')) : { entries: [], sources: [] };
  const exceptions = JSON.parse(readFileSync(join(grammarRoot, 'corpus-exceptions.json'), 'utf8')).exceptions;
  const minimumFiles = minimumFilesOf(readFileSync(join(grammarRoot, 'corpus-sources.psd1'), 'utf8'));

  const results = [];
  const perSource = new Map();
  const totals = Object.fromEntries(constructs.map(([name]) => [name, 0]));
  if (manifest.entries.length > 0) {
    const list = join(corpusDir, '.gate-files.txt');
    writeFileSync(list, manifest.entries.map(e => join(corpusDir, e.path)).join('\n'));
    const parsed = run(['parse', '--paths', list], packageDir);
    const output = parsed.stdout ?? '';
    const trees = parseCliOutput(output);
    const texts = splitTrees(output);
    if (trees.length !== manifest.entries.length || texts.length !== manifest.entries.length) {
      throw new Error(`expected ${manifest.entries.length} trees, got ${trees.length}; ${parsed.stderr}`);
    }
    manifest.entries.forEach((entry, i) => {
      const clean = trees[i].errors.length === 0;
      results.push({ path: entry.path, source: entry.source, clean });
      const stats = perSource.get(entry.source) ?? { files: 0, clean: 0, counts: Object.fromEntries(constructs.map(([name]) => [name, 0])) };
      stats.files++;
      if (clean) stats.clean++;
      for (const [name, count] of Object.entries(countConstructs(texts[i]))) {
        stats.counts[name] += count;
        totals[name] += count;
      }
      perSource.set(entry.source, stats);
    });
  }

  const verdict = evaluate({ minimumFiles, results, exceptions, counts: totals });
  const cli = JSON.parse(readFileSync(join(packageDir, 'node_modules', 'tree-sitter-cli', 'package.json'), 'utf8')).version;
  const grammar = spawnSync('git', ['rev-parse', '--short', 'HEAD'], { cwd: grammarRoot, encoding: 'utf8' }).stdout.trim() || 'unknown';
  const meta = { date: new Date().toISOString().slice(0, 10), cli, grammar, collected: manifest.generatedAt ?? 'never' };
  mkdirSync(dirname(out), { recursive: true });
  writeFileSync(out, renderReport({ meta, verdict, sources: manifest.sources, perSource, exceptions }));

  console.log(`${verdict.files - verdict.failing} of ${verdict.files} files parse without errors (${(verdict.cleanRate * 100).toFixed(1)}%); report: ${out}`);
  for (const problem of verdict.problems) console.error(`gate: ${problem}`);
  process.exitCode = verdict.passed ? 0 : 1;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  main();
}
