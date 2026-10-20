import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { test } from 'node:test';

const grammar = fileURLToPath(new URL('../tree-sitter-daml/', import.meta.url));
const binary = join(grammar, 'node_modules', 'tree-sitter-cli',
  process.platform === 'win32' ? 'tree-sitter.exe' : 'tree-sitter');

for (const [name, seed] of [
  ['colons: signature in let', 106],
  ['negative literal: do expressions and bind patterns', 84],
]) {
  test(`scanner restores ${name} after the failing edit batch`, () => {
    const run = spawnSync(binary, ['fuzz', '--iterations', '1', '--include', name], {
      cwd: grammar,
      encoding: 'utf8',
      env: { ...process.env, TREE_SITTER_SEED: String(seed) },
    });
    const output = run.stdout + run.stderr;
    assert.ifError(run.error);
    assert.equal(run.status, 0, output);
    assert.match(output, new RegExp(name));
    // The CLI can exit zero after reporting a failed fuzz case.
    assert.doesNotMatch(output, /Incorrect|Unexpected scope change|failed fuzzing/i);
  });
}
