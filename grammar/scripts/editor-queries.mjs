// Query files for editors whose conventions differ from those of queries/ (the tree-sitter CLI's
// and Neovim's), generated from queries/ so they can't drift.
//
// Helix (25.07 and later) lets the last matching pattern win as they do, but names highlights
// after its theme scopes, and a later capture of the same node under a name no theme scope matches
// takes the node's highlight away. Predicate-only captures such as `@_op` are such names. Its
// highlights are therefore ours with the captures renamed, without `@spell`, and without the
// patterns that need predicate-only captures. Injections carry over as they are. Its locals,
// indents and textobjects follow other conventions and are written by hand next to these.
//
//   node grammar/scripts/editor-queries.mjs            exit code 1 when a generated file is stale
//   node grammar/scripts/editor-queries.mjs --update   writes them
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const grammarDir = join(here, '..');
const queriesDir = join(grammarDir, 'tree-sitter-daml', 'queries');

/**
 * Splits a query file into its top-level patterns and the text between them (whitespace and
 * comments), keeping every character: the texts joined give the file back.
 */
export function splitQuery(source) {
  const segments = [];
  let i = 0;
  let patternStart = -1;
  let patternEnd = -1;
  let triviaStart = 0;

  // Moves past a string, comment or bracketed group starting at `at`.
  const skip = at => {
    if (source[at] === '"') {
      let k = at + 1;
      while (k < source.length && source[k] !== '"') k += source[k] === '\\' ? 2 : 1;
      return k + 1;
    }
    if (source[at] === ';') {
      const end = source.indexOf('\n', at);
      return end === -1 ? source.length : end;
    }
    let depth = 0;
    let k = at;
    do {
      const c = source[k];
      if (c === '"' || c === ';') {
        k = skip(k);
        continue;
      }
      if (c === '(' || c === '[') depth++;
      else if (c === ')' || c === ']') depth--;
      k++;
    } while (depth > 0 && k < source.length);
    return k;
  };
  const closePattern = () => {
    if (patternStart < 0) return;
    if (patternStart > triviaStart) segments.push({ kind: 'trivia', text: source.slice(triviaStart, patternStart) });
    segments.push({ kind: 'pattern', text: source.slice(patternStart, patternEnd) });
    triviaStart = patternEnd;
    patternStart = -1;
  };

  while (i < source.length) {
    const c = source[i];
    if (/\s/.test(c)) {
      i++;
    } else if (c === ';') {
      i = skip(i);
    } else if (c === '@' || c === '*' || c === '+' || c === '?') {
      // A capture or quantifier belongs to the pattern before it.
      let k = i + 1;
      while (k < source.length && /[\w.\-]/.test(source[k])) k++;
      patternEnd = k;
      i = k;
    } else {
      closePattern();
      patternStart = i;
      if (c === '(' || c === '[' || c === '"') {
        i = skip(i);
      } else {
        while (i < source.length && /[\w.\-]/.test(source[i])) i++;
        if (i === patternStart) throw new Error(`unexpected ${JSON.stringify(c)} at offset ${patternStart}`);
      }
      patternEnd = i;
    }
  }
  closePattern();
  if (triviaStart < source.length) segments.push({ kind: 'trivia', text: source.slice(triviaStart) });
  return segments;
}

/** The capture names in a pattern, outside strings and comments, in order. */
export function captures(pattern) {
  return [...scanCaptures(pattern)].map(c => c.name);
}

function* scanCaptures(text) {
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"') {
      i++;
      while (i < text.length && text[i] !== '"') i += text[i] === '\\' ? 2 : 1;
    } else if (c === ';') {
      while (i < text.length && text[i] !== '\n') i++;
    } else if (c === '@') {
      let k = i + 1;
      while (k < text.length && /[\w.\-]/.test(text[k])) k++;
      yield { start: i, end: k, name: text.slice(i + 1, k) };
      i = k - 1;
    }
  }
}

/** The pattern with each capture renamed by `rename`, or removed (with the spaces before it) where it gives null. */
export function renameCaptures(pattern, rename) {
  let out = '';
  let last = 0;
  for (const { start, end, name } of scanCaptures(pattern)) {
    const to = rename(name);
    if (to === name) continue;
    let from = start;
    if (to === null) while (from > last && /[ \t]/.test(pattern[from - 1])) from--;
    out += pattern.slice(last, from) + (to === null ? '' : `@${to}`);
    last = end;
  }
  return out + pattern.slice(last);
}

// The highlight scopes of Helix 25.07 (its themes guide, markup and diff left out). A capture
// gets the longest scope that is a dotted prefix of its name.
export const helixScopes = new Set(`
  attribute type type.builtin type.parameter type.enum type.enum.variant constructor
  constant constant.builtin constant.builtin.boolean constant.character constant.character.escape
  constant.numeric constant.numeric.integer constant.numeric.float
  string string.regexp string.special string.special.path string.special.url string.special.symbol
  comment comment.line comment.line.documentation comment.block comment.block.documentation comment.unused
  variable variable.builtin variable.parameter variable.other variable.other.member variable.other.member.private
  label punctuation punctuation.delimiter punctuation.bracket punctuation.special
  keyword keyword.control keyword.control.conditional keyword.control.repeat keyword.control.import
  keyword.control.return keyword.control.exception keyword.operator keyword.directive keyword.function
  keyword.storage keyword.storage.type keyword.storage.modifier
  operator function function.builtin function.method function.method.private function.macro function.special
  tag tag.builtin namespace special`.trim().split(/\s+/));

/** The Helix scope a capture name ends up with, or null when none matches. */
export function helixScope(name) {
  for (let candidate = name; candidate; candidate = candidate.slice(0, Math.max(0, candidate.lastIndexOf('.')))) {
    if (helixScopes.has(candidate)) return candidate;
  }
  return null;
}

// Our names (Neovim's) where Helix has a more specific scope than the prefix fallback would give,
// or none at all. The rest fall back well: `function.call` to `function`, `keyword.contract`
// to `keyword` (a theme can still colour `keyword.contract` itself).
export const helixRenames = {
  module: 'namespace',
  number: 'constant.numeric.integer',
  'number.float': 'constant.numeric.float',
  character: 'constant.character',
  boolean: 'constant.builtin.boolean',
  'keyword.conditional': 'keyword.control.conditional',
  'keyword.repeat': 'keyword.control.repeat',
  'keyword.import': 'keyword.control.import',
  'keyword.exception': 'keyword.control.exception',
  'keyword.declaration': 'keyword.storage.type',
  'variable.member': 'variable.other.member',
  spell: null,
};

/** Helix's highlights from ours: renamed captures, no `@spell`, no patterns with predicate-only captures. */
export function helixHighlights(source) {
  let out = '';
  for (const segment of splitQuery(source)) {
    if (segment.kind === 'trivia') {
      out += segment.text;
      continue;
    }
    const renamed = renameCaptures(segment.text, name => (Object.hasOwn(helixRenames, name) ? helixRenames[name] : name));
    const names = captures(renamed);
    if (names.length === 0 || names.some(n => n.startsWith('_'))) {
      // The comment right above a pattern goes with it.
      out = out.replace(/(^|\n)(?:[ \t]*;[^\n]*\n)+$/, '$1');
      continue;
    }
    out += renamed;
  }
  return out.replace(/\n{3,}/g, '\n\n');
}

const generatedNote = (source, ...how) =>
  [`; Generated from queries/${source} by grammar/scripts/editor-queries.mjs: edit that file and run`,
    '; the script with --update.', ...how].join('\n') + '\n\n';

/** The generated files: where each goes and what it holds. */
export function generatedFiles() {
  const read = name => readFileSync(join(queriesDir, name), 'utf8').replace(/\r\n/g, '\n');
  const helix = join(grammarDir, 'editors', 'helix', 'queries', 'daml');
  return [
    {
      path: join(helix, 'highlights.scm'),
      content: generatedNote('highlights.scm',
        '; Captures renamed to Helix theme scopes; `@spell` and the patterns that need predicate-only',
        '; captures (`@_op`, `@_name`) left out, since Helix would take those nodes\' highlight away.') +
        helixHighlights(read('highlights.scm')),
    },
    { path: join(helix, 'injections.scm'), content: generatedNote('injections.scm') + read('injections.scm') },
  ];
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const update = process.argv.includes('--update');
  let stale = 0;
  for (const { path, content } of generatedFiles()) {
    const current = existsSync(path) ? readFileSync(path, 'utf8').replace(/\r\n/g, '\n') : null;
    if (current === content) continue;
    if (update) {
      mkdirSync(dirname(path), { recursive: true });
      writeFileSync(path, content);
      console.log(`wrote ${relative(process.cwd(), path)}`);
    } else {
      console.error(`${relative(process.cwd(), path)} is stale: run node grammar/scripts/editor-queries.mjs --update`);
      stale++;
    }
  }
  process.exitCode = stale === 0 ? 0 : 1;
}
