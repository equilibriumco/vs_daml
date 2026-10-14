// Seeds for the grammar's fuzzer (grammar/fuzz/fuzzer.c): the inputs of the corpus tests
// (tree-sitter-daml/test/corpus/**/*.txt) and, when it has been collected, the real-world corpus.
// One file per seed, named by its content, so equal seeds are one file.
//
//   node grammar/scripts/fuzz-seeds.mjs <out dir>
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const testsDir = join(here, '..', 'tree-sitter-daml', 'test', 'corpus');
const corpusDir = join(here, '..', 'test', 'real-world');

const header = /^={3,}\s*$/;
const separator = /^-{3,}\s*$/;

/** The inputs of the tests in a corpus test file: what lies between a test's name and its `---`. */
export function testInputs(text) {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const inputs = [];
  let i = 0;
  while (i < lines.length) {
    // A header: a line of `=`, the name, another line of `=`.
    if (!header.test(lines[i])) {
      i++;
      continue;
    }
    i++;
    while (i < lines.length && !header.test(lines[i])) i++;
    i++;
    const start = i;
    while (i < lines.length && !separator.test(lines[i])) i++;
    // tree-sitter trims the blank lines around an input; so do we, keeping one final line break.
    const input = lines.slice(start, i).join('\n').replace(/^\n+/, '').replace(/\n+$/, '');
    if (input) inputs.push(input + '\n');
  }
  return inputs;
}

/** A seed's file name: the start of its content's SHA-256. */
export function seedName(content) {
  return createHash('sha256').update(content).digest('hex').slice(0, 16) + '.daml';
}

function testFiles(dir) {
  const found = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) found.push(...testFiles(join(dir, entry.name)));
    else if (entry.name.endsWith('.txt')) found.push(join(dir, entry.name));
  }
  return found;
}

function main() {
  const out = process.argv[2];
  if (!out) {
    console.error('usage: node grammar/scripts/fuzz-seeds.mjs <out dir>');
    process.exitCode = 2;
    return;
  }
  mkdirSync(out, { recursive: true });
  const seeds = new Set();
  const add = content => {
    const name = seedName(content);
    if (!seeds.has(name)) {
      seeds.add(name);
      writeFileSync(join(out, name), content);
    }
  };
  for (const file of testFiles(testsDir)) {
    for (const input of testInputs(readFileSync(file, 'utf8'))) add(input);
  }
  const fromTests = seeds.size;
  const manifest = join(corpusDir, 'manifest.json');
  if (existsSync(manifest)) {
    for (const entry of JSON.parse(readFileSync(manifest, 'utf8')).entries) {
      add(readFileSync(join(corpusDir, entry.path)));
    }
  }
  console.log(`${seeds.size} seeds in ${out}: ${fromTests} from the corpus tests, ${seeds.size - fromTests} from the real-world corpus`);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  main();
}
