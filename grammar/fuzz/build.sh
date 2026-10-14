#!/usr/bin/env bash
# Builds the Daml grammar's fuzzer (fuzzer.c) with clang, libFuzzer and the address and
# undefined-behaviour sanitizers: the tree-sitter runtime pinned in grammar/tree-sitter-runtime.txt
# (the release of the pinned CLI), the generated parser and the scanner. Run
# `npx tree-sitter generate` in grammar/tree-sitter-daml first.
#
#   grammar/fuzz/build.sh [out dir]        (default grammar/fuzz/out)
#
# Then, for example:
#   node grammar/scripts/fuzz-seeds.mjs seeds
#   grammar/fuzz/out/daml-fuzzer -runs=0 seeds                        every seed once
#   grammar/fuzz/out/daml-fuzzer -max_total_time=600 -dict=grammar/fuzz/daml.dict work seeds
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
grammar="$here/../tree-sitter-daml"
out="${1:-$here/out}"
mkdir -p "$out"

runtime_commit="$(sed -n 's/^commit=\([0-9a-f]*\).*/\1/p' "$here/../tree-sitter-runtime.txt")"
runtime="$out/tree-sitter"
if [ "$(git -C "$runtime" rev-parse HEAD 2>/dev/null || true)" != "$runtime_commit" ]; then
  rm -rf "$runtime"
  mkdir -p "$runtime"
  git -C "$runtime" init -q
  git -C "$runtime" remote add origin https://github.com/tree-sitter/tree-sitter
  git -C "$runtime" sparse-checkout set --no-cone /lib/src/ /lib/include/
  git -C "$runtime" fetch -q --depth 1 origin "$runtime_commit"
  git -C "$runtime" checkout -q FETCH_HEAD
fi

if [ ! -f "$grammar/src/parser.c" ]; then
  echo "No generated parser: run npx tree-sitter generate in grammar/tree-sitter-daml." >&2
  exit 1
fi

probe="$out/probe"
if ! printf 'int LLVMFuzzerTestOneInput(const char *d, unsigned long n) { return 0; }\n' |
    clang -x c -fsanitize=fuzzer -o "$probe" - 2>/dev/null; then
  echo "clang can't build a libFuzzer target here; install clang with its runtime (on Ubuntu: libclang-rt-dev)." >&2
  exit 1
fi

sanitizers=(-g -O1 -fno-omit-frame-pointer -fsanitize=address,undefined -fno-sanitize-recover=undefined)
# The scanner, the runtime and the harness are what the fuzzer explores, so they get coverage;
# the generated parser is mostly tables, and coverage there would only slow the build down.
coverage=(-fsanitize=fuzzer-no-link)

clang "${sanitizers[@]}" "${coverage[@]}" -I "$runtime/lib/include" -I "$runtime/lib/src" -c "$runtime/lib/src/lib.c" -o "$out/runtime.o"
clang "${sanitizers[@]}" -I "$grammar/src" -c "$grammar/src/parser.c" -o "$out/parser.o"
clang "${sanitizers[@]}" "${coverage[@]}" -I "$grammar/src" -c "$grammar/src/scanner.c" -o "$out/scanner.o"
clang "${sanitizers[@]}" "${coverage[@]}" -I "$runtime/lib/include" -c "$here/fuzzer.c" -o "$out/fuzzer.o"
clang "${sanitizers[@]}" -fsanitize=fuzzer "$out/runtime.o" "$out/parser.o" "$out/scanner.o" "$out/fuzzer.o" -o "$out/daml-fuzzer"
echo "Built $out/daml-fuzzer"
