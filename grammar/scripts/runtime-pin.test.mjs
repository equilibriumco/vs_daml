import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const grammar = join(dirname(fileURLToPath(import.meta.url)), '..');

// The fuzzer and the native library build the runtime from this pin, the tests run the CLI from
// package.json: bumping one without the other would ship a parser the tests never ran.
test('the runtime pin is the release of the pinned CLI', () => {
  const pin = Object.fromEntries(
    readFileSync(join(grammar, 'tree-sitter-runtime.txt'), 'utf8')
      .split(/\r?\n/)
      .filter(line => /^\w+=/.test(line))
      .map(line => line.split('=')));
  const cli = JSON.parse(readFileSync(join(grammar, 'tree-sitter-daml', 'package.json'), 'utf8')).devDependencies['tree-sitter-cli'];

  assert.equal(pin.version, cli);
  assert.match(pin.commit, /^[0-9a-f]{40}$/);
});
