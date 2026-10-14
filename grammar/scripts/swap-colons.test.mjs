// Tests of swap-colons.mjs: Haskell source turned into Daml's colons (`:` <-> `::`), and corpus
// test files whose input is rewritten while their expected trees are left alone.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { swapColons, swapCorpusFile } from './swap-colons.mjs';

test('signatures, record fields and cons swap their colons', () => {
  assert.equal(swapColons('f :: Int -> Int'), 'f : Int -> Int');
  assert.equal(swapColons('data R = R { a :: Int, b :: [a] }'), 'data R = R { a : Int, b : [a] }');
  assert.equal(swapColons('xs = 1 : 2 : []'), 'xs = 1 :: 2 :: []');
  assert.equal(swapColons('f (x:xs) = x'), 'f (x::xs) = x');
  assert.equal(swapColons('g = (:) 1 []'), 'g = (::) 1 []');
  assert.equal(swapColons('h = (::)'), 'h = (:)');
});

test('other operators that contain colons stay as they are', () => {
  assert.equal(swapColons('data T = Int :| Int'), 'data T = Int :| Int');
  assert.equal(swapColons('x = a :+: b ::: c'), 'x = a :+: b ::: c');
  assert.equal(swapColons('y = a <: b'), 'y = a <: b');
});

test('strings, characters and comments keep their colons', () => {
  assert.equal(swapColons('s = "a :: b : c" :: String'), 's = "a :: b : c" : String');
  assert.equal(swapColons("c = ':' :: Char"), "c = ':' : Char");
  assert.equal(swapColons("e = '\\'' : []"), "e = '\\'' :: []");
  assert.equal(swapColons('f :: Int -- a :: b'), 'f : Int -- a :: b');
  assert.equal(swapColons('{- x :: y {- nested : z -} :: w -} g :: Int'), '{- x :: y {- nested : z -} :: w -} g : Int');
});

test('a run of dashes is a comment only when it stands alone', () => {
  assert.equal(swapColons('a --> b :: c'), 'a --> b : c');
  assert.equal(swapColons('a ---- b :: c'), 'a ---- b :: c');
});

test('primes in names are not character literals', () => {
  assert.equal(swapColons("f' :: a' -> a'"), "f' : a' -> a'");
  assert.equal(swapColons("x = f' : g'"), "x = f' :: g'");
});

test('a qualified cons swaps too; composition with a qualified name does not', () => {
  assert.equal(swapColons('a = a A.: a'), 'a = a A.:: a');
  assert.equal(swapColons('b = b GHC.Types.:: b'), 'b = b GHC.Types.: b');
  assert.equal(swapColons('c = M.f . g'), 'c = M.f . g');
  assert.equal(swapColons('d = x.: y'), 'd = x.: y');
});

test('the unicode colon is left alone', () => {
  assert.equal(swapColons('f ∷ Int'), 'f ∷ Int');
});

const corpusFile = [
  '================================================================================',
  'signature: simple',
  '================================================================================',
  '',
  'f :: Int',
  '',
  '--------------------------------------------------------------------------------',
  '',
  '(daml',
  '  (declarations',
  '    (signature',
  '      (variable)',
  '      (name))))',
  '',
  '================================================================================',
  'exp: cons',
  '================================================================================',
  '',
  'a = 1 : []',
  '',
  '--------------------------------------------------------------------------------',
  '',
  '(daml (declarations (bind (variable) (match (infix (literal (integer)) (constructor_operator) (list))))))',
  '',
].join('\n');

test('a corpus file has its inputs swapped and its expected trees kept', () => {
  const swapped = swapCorpusFile(corpusFile);

  assert.ok(swapped.includes('\nf : Int\n'));
  assert.ok(swapped.includes('\na = 1 :: []\n'));
  assert.equal(swapped.split('\n').filter(l => l.startsWith('====')).length, 4, 'headers kept');
  assert.ok(swapped.includes('(constructor_operator)'), 'expected trees untouched');
  assert.equal(swapCorpusFile(swapCorpusFile(corpusFile)), corpusFile, 'swapping twice gives the original');
});
