// Checks the WebAssembly build of the grammar (`tree-sitter build --wasm`) before it is published:
// it compiles, it is a dynamically linked side module (what web-tree-sitter's Language.load
// takes), and it exports the language. Uses only the WebAssembly API built into Node.
//
//   node grammar/scripts/check-wasm.mjs tree-sitter-daml.wasm        exit code 1 on a problem
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

/** The problems of a grammar's module, empty when there are none. */
export async function wasmProblems(bytes, language = 'daml') {
  let module;
  try {
    module = await WebAssembly.compile(bytes);
  } catch (error) {
    return [`not a valid WebAssembly module: ${error.message}`];
  }
  const problems = [];
  if (WebAssembly.Module.customSections(module, 'dylink.0').length === 0) {
    problems.push('not a side module: no dylink.0 section, so web-tree-sitter cannot load it');
  }
  const exported = WebAssembly.Module.exports(module).map(e => e.name);
  if (!exported.includes(`tree_sitter_${language}`)) {
    problems.push(`tree_sitter_${language} is not exported`);
  }
  return problems;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const file = process.argv[2];
  if (!file) {
    console.error('usage: node check-wasm.mjs <module.wasm>');
    process.exit(2);
  }
  const bytes = readFileSync(file);
  const problems = await wasmProblems(bytes);
  for (const problem of problems) console.error(`${file}: ${problem}`);
  if (problems.length === 0) console.log(`${file}: ${(bytes.length / 1024).toFixed(0)} KiB, side module exporting tree_sitter_daml`);
  process.exitCode = problems.length === 0 ? 0 : 1;
}
