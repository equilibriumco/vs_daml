import { test } from 'node:test';
import assert from 'node:assert/strict';
import { applyEdits, toCliEdit, inverseEdit, randomGenerator, randomEdits, sourceFiles, checkCases, compareTrees } from './edit-properties.mjs';

test('edits apply one after another, each to the text the one before left', () => {
  const text = 'f = 1\ng = 2\n';
  const edits = [
    { position: 10, delete: 1, insert: '37' },
    { position: 0, delete: 0, insert: 'h = 0\n' },
  ];

  assert.equal(applyEdits(text, edits), 'h = 0\nf = 1\ng = 37\n');
});

test('an edit is given to the CLI as row, byte column, byte count and text', () => {
  const text = 'x = "żółw"\ny = 1\n';

  // After the four letters of "żółw", three of them two bytes long.
  assert.equal(toCliEdit(text, { position: 9, delete: 1, insert: 'X' }), '0,12 1 X');
  assert.equal(toCliEdit(text, { position: 5, delete: 2, insert: '' }), '0,5 4 ');
  assert.equal(toCliEdit(text, { position: 15, delete: 0, insert: '\n  ' }), '1,4 0 \n  ');
});

test('the generator gives the same edits for the same seed', () => {
  const text = 'template T\n  with\n    p : Party\n  where\n    signatory p\n';
  const first = randomEdits(text, randomGenerator(42));
  const again = randomEdits(text, randomGenerator(42));

  assert.deepEqual(first, again);
  assert.ok(first.length >= 1 && first.length <= 3);
});

test('generated edits stay inside the text and never split a character', () => {
  const text = 'a = "😀 ∀ x"\nb = 2\n';
  const rand = randomGenerator(7);
  for (let i = 0; i < 500; i++) {
    let current = text;
    for (const edit of randomEdits(current, rand)) {
      assert.ok(edit.position >= 0 && edit.position + edit.delete <= current.length, JSON.stringify(edit));
      const after = applyEdits(current, [edit]);
      assert.ok(!/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/.test(after), `split a character: ${JSON.stringify(edit)}`);
      current = after;
    }
  }
});

test('an edit and its inverse give the text back', () => {
  const text = 'f = 1\ng = 2\n';
  const edit = { position: 4, delete: 3, insert: 'x\n  y' };
  const edited = applyEdits(text, [edit]);

  assert.equal(applyEdits(edited, [inverseEdit(text, edit)]), text);
});

// The edited text of the case that shows the inherited layout behaviour: inserting `il` at 1,7 and
// parsing incrementally ends the `case` block at the end of line 2, a parse from scratch only
// before `where`.
const caseBeforeWhere = 'f xs ys = case (xs, ys) of\n  ([], il[]) -> []\n  (_, _) -> xs\n  where\n    g = 1\n';

test('a node may end elsewhere when only whitespace lies between the two ends', () => {
  const incremental = '(daml [0, 0] - [5, 0]\n  match: (match [0, 8] - [2, 14]\n    expression: (case [0, 10] - [2, 14]';
  const full = '(daml [0, 0] - [5, 0]\n  match: (match [0, 8] - [3, 2]\n    expression: (case [0, 10] - [3, 2]';

  assert.deepEqual(compareTrees(incremental, full, caseBeforeWhere), { same: true, tolerated: true });
  assert.deepEqual(compareTrees(full, full, caseBeforeWhere), { same: true, tolerated: false });
});

test('anything but whitespace between the ends, or another start, kind, field or nesting, is a difference', () => {
  const tree = end => `(daml [0, 0] - [5, 0]\n  match: (match [0, 8] - ${end})`;
  const differs = (first, second) => assert.equal(compareTrees(first, second, caseBeforeWhere).same, false, `${first}\n${second}`);

  // Two characters of `where` lie between [3, 2] and [3, 4].
  differs(tree('[2, 14]'), tree('[3, 4]'));
  differs(tree('[3, 2]'), '(daml [0, 0] - [5, 0]\n  match: (match [0, 9] - [3, 2])');
  differs(tree('[3, 2]'), '(daml [0, 0] - [5, 0]\n  match: (case [0, 8] - [3, 2])');
  differs(tree('[3, 2]'), '(daml [0, 0] - [5, 0]\n  body: (match [0, 8] - [3, 2])');
  differs(tree('[3, 2]'), '(daml [0, 0] - [5, 0]\n  match: (match [0, 8] - [3, 2]))');
  differs(tree('[3, 2]'), `${tree('[3, 2]')}\n  (variable [4, 4] - [4, 5])`);
});

test('the columns of a range count bytes', () => {
  // `x = "żółw"` takes 13 bytes, three of the letters two each; two spaces follow.
  const text = 'x = "żółw"  \ny\n';
  const node = end => `(string [0, 4] - ${end})`;

  assert.equal(compareTrees(node('[0, 13]'), node('[0, 15]'), text).same, true);
  assert.equal(compareTrees(node('[0, 12]'), node('[0, 15]'), text).same, false);
});

test('after random edits, an incremental parse gives what a full parse gives, and undoing them the original', () => {
  const files = sourceFiles();
  assert.ok(files.length > 0, 'no Daml files to edit');

  const { failures, exact } = checkCases({ files, seed: 20261014, cases: 40 });

  assert.deepEqual(failures, []);
  assert.ok(exact > 0, 'no case left the text without errors, so nothing was compared exactly');
});
