#!/usr/bin/env node
// Daml swaps Haskell's colons: `:` annotates a type and `::` is list cons. This turns Haskell
// source into Daml's colons, for the test files inherited from tree-sitter-haskell (now and when
// taking upstream changes):
//
//   node grammar/scripts/swap-colons.mjs <corpus file>...
//
// Only an operator that is exactly `:` or `::` changes; `:|`, `:::`, `<:` and the like don't, nor
// does anything in strings, character literals or comments. In a corpus file only the inputs
// change: expected trees name nodes, not the colons between them, so they stay valid.
import { readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const symbolChar = /[!#$%&*+./<=>?@\\^|\-~:\p{S}]/u;
const identChar = /[\p{L}\p{N}_']/u;
const charLiteral = /^'(\\[^'\n]+|[^'\\\n])'/;

/** Haskell source with `:` and `::` swapped, outside strings, characters and comments. */
export function swapColons(source) {
  let out = '';
  let i = 0;
  while (i < source.length) {
    const rest = source.slice(i);
    if (rest.startsWith('{-')) {
      // Block comments nest.
      let depth = 0;
      let j = i;
      do {
        if (source.startsWith('{-', j)) {
          depth++;
          j += 2;
        } else if (source.startsWith('-}', j)) {
          depth--;
          j += 2;
        } else {
          j++;
        }
      } while (depth > 0 && j < source.length);
      out += source.slice(i, j);
      i = j;
      continue;
    }
    const c = source[i];
    if (c === '"') {
      let j = i + 1;
      while (j < source.length && source[j] !== '"') {
        j += source[j] === '\\' ? 2 : 1;
      }
      out += source.slice(i, j + 1);
      i = j + 1;
      continue;
    }
    if (c === "'") {
      const literal = i === 0 || !identChar.test(source[i - 1]) ? charLiteral.exec(rest) : null;
      if (literal) {
        out += literal[0];
        i += literal[0].length;
        continue;
      }
      out += c;
      i++;
      continue;
    }
    if (symbolChar.test(c)) {
      let j = i;
      while (j < source.length && symbolChar.test(source[j])) {
        j++;
      }
      const run = source.slice(i, j);
      // `A.:` is the cons of module A: the dot after a module name belongs to the name.
      if (run.length > 1 && run[0] === '.' && /^[\p{Lu}][\p{L}\p{N}_']*$/u.test(/[\p{L}\p{N}_'.]*$/u.exec(source.slice(0, i))[0].split('.').pop())) {
        const op = run.slice(1);
        out += '.' + (op === ':' ? '::' : op === '::' ? ':' : op);
        i = j;
        continue;
      }
      if (/^--+$/.test(run)) {
        // A line comment: two or more dashes and nothing else of an operator.
        const end = source.indexOf('\n', i);
        const stop = end === -1 ? source.length : end;
        out += source.slice(i, stop);
        i = stop;
        continue;
      }
      out += run === ':' ? '::' : run === '::' ? ':' : run;
      i = j;
      continue;
    }
    out += c;
    i++;
  }
  return out;
}

const header = /^={3,}/;
const divider = /^-{3,}\s*$/;

/** A tree-sitter corpus file with the colons of its inputs swapped; expected trees unchanged. */
export function swapCorpusFile(text) {
  const lines = text.split('\n');
  const out = [];
  let input = [];
  let state = 'expected';
  const flush = () => {
    if (input.length > 0) {
      out.push(...swapColons(input.join('\n')).split('\n'));
      input = [];
    }
  };
  for (let n = 0; n < lines.length; n++) {
    const line = lines[n];
    if (state === 'expected' && header.test(line)) {
      // Header: a line of `=`, the name, another line of `=`.
      out.push(line, lines[n + 1], lines[n + 2]);
      n += 2;
      state = 'input';
      continue;
    }
    if (state === 'input' && divider.test(line)) {
      flush();
      out.push(line);
      state = 'expected';
      continue;
    }
    if (state === 'input') {
      input.push(line);
    } else {
      out.push(line);
    }
  }
  flush();
  return out.join('\n');
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const files = process.argv.slice(2);
  if (files.length === 0) {
    console.error('usage: node grammar/scripts/swap-colons.mjs <corpus file>...');
    process.exit(2);
  }
  for (const file of files) {
    const before = readFileSync(file, 'utf8');
    const after = swapCorpusFile(before);
    if (after !== before) {
      writeFileSync(file, after);
      console.log(`swapped: ${file}`);
    }
  }
}
