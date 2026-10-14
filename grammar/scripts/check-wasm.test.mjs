import { test } from 'node:test';
import assert from 'node:assert/strict';
import { wasmProblems } from './check-wasm.mjs';

// The smallest modules that show each case: one empty function, maybe exported, maybe with the
// dylink.0 section a side module starts with.
const name = text => [text.length, ...Buffer.from(text)];
const section = (id, bytes) => [id, bytes.length, ...bytes];
function wasm({ exportName, sideModule }) {
  return new Uint8Array([
    0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00,
    ...(sideModule ? section(0, name('dylink.0')) : []),
    ...section(1, [1, 0x60, 0, 0]),
    ...section(3, [1, 0]),
    ...(exportName ? section(7, [1, ...name(exportName), 0x00, 0]) : []),
    ...section(10, [1, 2, 0, 0x0b]),
  ]);
}

test('a side module that exports the language passes', async () => {
  assert.deepEqual(await wasmProblems(wasm({ exportName: 'tree_sitter_daml', sideModule: true })), []);
});

test('a module that is not a side module, or exports something else, is reported', async () => {
  const notSide = await wasmProblems(wasm({ exportName: 'tree_sitter_daml', sideModule: false }));
  const otherExport = await wasmProblems(wasm({ exportName: 'tree_sitter_haskell', sideModule: true }));

  assert.equal(notSide.length, 1);
  assert.match(notSide[0], /side module/);
  assert.deepEqual(otherExport, ['tree_sitter_daml is not exported']);
});

test('bytes that are not WebAssembly are reported, not thrown', async () => {
  const problems = await wasmProblems(new Uint8Array([0x7f, 0x45, 0x4c, 0x46]));

  assert.equal(problems.length, 1);
  assert.match(problems[0], /not a valid WebAssembly module/);
});
