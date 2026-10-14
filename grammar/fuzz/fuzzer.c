// Fuzzing the Daml grammar with libFuzzer, under the address and undefined-behaviour sanitizers.
//
// The input is Daml source. It is parsed, edited one to three times (where and how is read from a
// hash of the input, so a mutation of the input moves the edits too) and parsed again
// incrementally from the edited old tree. Besides what the sanitizers catch in the scanner and the
// runtime, two properties are checked:
//
// - When the edited text parses without errors, the incremental tree is the tree a parse from
//   scratch gives: the same nodes over the same bytes.
// - Undoing the edits, again incrementally, gives back the tree of the input (when the input
//   parses without errors).
//
// Where an edit leaves errors, the trees may differ: error recovery depends on which old subtrees
// the parser reuses, and tree-sitter doesn't promise the same recovery both ways.

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "tree_sitter/api.h"

const TSLanguage *tree_sitter_daml(void);

// Long inputs make slower runs, not more interesting ones; every real-world file fits.
enum { MAX_INPUT = 1 << 20, MAX_EDITS = 3 };

typedef struct {
  char *data;
  uint32_t length;
} Text;

typedef struct {
  uint32_t start;
  uint32_t removed;
  char *inserted;
  uint32_t inserted_length;
} Edit;

static const char *const daml_words[] = {
  "with", "where", "do", "->", "(", ")", " ", "choice", "template", ":", "=", "..",
  "controller", ",", "signatory", "interface", "\"", "--", "{-", "-}", "\n", "\t",
};

static uint64_t hash(const uint8_t *data, size_t size) {
  uint64_t h = 1469598103934665603ULL;
  for (size_t i = 0; i < size; i++) {
    h = (h ^ data[i]) * 1099511628211ULL;
  }
  return h;
}

// splitmix64, seeded by the hash of the input.
static uint64_t next(uint64_t *state) {
  uint64_t z = (*state += 0x9E3779B97F4A7C15ULL);
  z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
  z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
  return z ^ (z >> 31);
}

static uint32_t below(uint64_t *state, uint32_t bound) {
  return bound == 0 ? 0 : (uint32_t)(next(state) % bound);
}

static TSPoint point_at(const char *text, uint32_t offset) {
  TSPoint point = {0, 0};
  for (uint32_t i = 0; i < offset; i++) {
    if (text[i] == '\n') {
      point.row++;
      point.column = 0;
    } else {
      point.column++;
    }
  }
  return point;
}

static char *copy_bytes(const char *bytes, uint32_t length) {
  char *copy = malloc(length + 1);
  if (copy == NULL) abort();
  memcpy(copy, bytes, length);
  copy[length] = '\0';
  return copy;
}

// A deletion, a piece of the text copied elsewhere, a line break with indentation, or a Daml word
// in place of a few bytes. Positions are bytes and may fall inside a character: the parser must
// cope with broken UTF-8 too.
static Edit random_edit(const Text *text, uint64_t *state) {
  Edit edit = {below(state, text->length + 1), 0, NULL, 0};
  uint32_t room = text->length - edit.start;
  switch (below(state, 4)) {
    case 0:
      edit.removed = room < 1 ? 0 : 1 + below(state, room < 20 ? room : 20);
      edit.inserted = copy_bytes("", 0);
      break;
    case 1: {
      uint32_t from = below(state, text->length + 1);
      uint32_t left = text->length - from;
      uint32_t length = left < 1 ? 0 : 1 + below(state, left < 20 ? left : 20);
      edit.inserted = copy_bytes(text->data + from, length);
      edit.inserted_length = length;
      break;
    }
    case 2: {
      uint32_t indent = below(state, 9);
      char buffer[10] = "\n";
      memset(buffer + 1, ' ', indent);
      edit.inserted = copy_bytes(buffer, 1 + indent);
      edit.inserted_length = 1 + indent;
      break;
    }
    default: {
      const char *word = daml_words[below(state, sizeof daml_words / sizeof daml_words[0])];
      edit.removed = below(state, (room < 10 ? room : 10) + 1);
      edit.inserted_length = (uint32_t)strlen(word);
      edit.inserted = copy_bytes(word, edit.inserted_length);
      break;
    }
  }
  return edit;
}

// The edit that takes the result of `edit` back to `text`.
static Edit inverse(const Text *text, const Edit *edit) {
  Edit undo = {edit->start, edit->inserted_length, copy_bytes(text->data + edit->start, edit->removed), edit->removed};
  return undo;
}

// Applies `edit` to the text and tells the tree about it.
static void apply(Text *text, TSTree *tree, const Edit *edit) {
  uint32_t length = text->length - edit->removed + edit->inserted_length;
  char *data = malloc(length + 1);
  if (data == NULL) abort();
  memcpy(data, text->data, edit->start);
  memcpy(data + edit->start, edit->inserted, edit->inserted_length);
  memcpy(data + edit->start + edit->inserted_length, text->data + edit->start + edit->removed,
         text->length - edit->start - edit->removed);
  data[length] = '\0';

  TSInputEdit input_edit = {
    .start_byte = edit->start,
    .old_end_byte = edit->start + edit->removed,
    .new_end_byte = edit->start + edit->inserted_length,
    .start_point = point_at(text->data, edit->start),
    .old_end_point = point_at(text->data, edit->start + edit->removed),
    .new_end_point = point_at(data, edit->start + edit->inserted_length),
  };
  ts_tree_edit(tree, &input_edit);

  free(text->data);
  text->data = data;
  text->length = length;
}

static bool only_whitespace(const Text *text, uint32_t from, uint32_t to) {
  for (uint32_t i = from; i < to && i < text->length; i++) {
    char c = text->data[i];
    if (c != ' ' && c != '\t' && c != '\n' && c != '\r') return false;
  }
  return true;
}

// The same kind of node starting at the same byte. The ends may differ by whitespace only: the
// layout scanner inherited from tree-sitter-haskell places the zero-width end of a block before or
// after the line break and indentation that follow it depending on whether old subtrees are
// reused (`f xs = case xs of` with alternatives and a `where` after them shows it), which moves
// only where the block's nodes end.
static bool same_node(TSNode a, TSNode b, const Text *text) {
  uint32_t end_a = ts_node_end_byte(a);
  uint32_t end_b = ts_node_end_byte(b);
  return ts_node_symbol(a) == ts_node_symbol(b) && ts_node_start_byte(a) == ts_node_start_byte(b) &&
         ts_node_is_missing(a) == ts_node_is_missing(b) &&
         (end_a == end_b || only_whitespace(text, end_a < end_b ? end_a : end_b, end_a < end_b ? end_b : end_a));
}

// The same nodes over the same bytes of `text`, walked in step without recursion.
static bool same_tree(TSNode a, TSNode b, const Text *text) {
  TSTreeCursor ca = ts_tree_cursor_new(a);
  TSTreeCursor cb = ts_tree_cursor_new(b);
  bool same = true;
  for (;;) {
    if (!same_node(ts_tree_cursor_current_node(&ca), ts_tree_cursor_current_node(&cb), text)) {
      same = false;
      break;
    }
    bool down_a = ts_tree_cursor_goto_first_child(&ca);
    bool down_b = ts_tree_cursor_goto_first_child(&cb);
    if (down_a != down_b) {
      same = false;
      break;
    }
    if (down_a) continue;
    bool done = false;
    for (;;) {
      bool across_a = ts_tree_cursor_goto_next_sibling(&ca);
      bool across_b = ts_tree_cursor_goto_next_sibling(&cb);
      if (across_a != across_b) {
        same = false;
        done = true;
        break;
      }
      if (across_a) break;
      bool up_a = ts_tree_cursor_goto_parent(&ca);
      bool up_b = ts_tree_cursor_goto_parent(&cb);
      if (up_a != up_b) same = false;
      if (!up_a || !up_b) {
        done = true;
        break;
      }
    }
    if (done) break;
  }
  ts_tree_cursor_delete(&ca);
  ts_tree_cursor_delete(&cb);
  return same;
}

// The edits as `tree-sitter parse --edits` takes them (row,column deleted inserted, in bytes, each
// on the text the ones before left), so a finding can be replayed outside the fuzzer.
typedef struct {
  char lines[MAX_EDITS][160];
  uint32_t count;
} EditLog;

static void log_edit(EditLog *log, const Text *text, const Edit *edit) {
  TSPoint at = point_at(text->data, edit->start);
  int used = snprintf(log->lines[log->count], sizeof log->lines[0], "%u,%u %u ", at.row, at.column, edit->removed);
  for (uint32_t i = 0; i < edit->inserted_length && used < (int)sizeof log->lines[0] - 5; i++) {
    unsigned char c = (unsigned char)edit->inserted[i];
    used += c == '\n' ? snprintf(log->lines[log->count] + used, 3, "\\n")
          : c < 32 || c > 126 ? snprintf(log->lines[log->count] + used, 5, "\\x%02x", c)
          : snprintf(log->lines[log->count] + used, 2, "%c", c);
  }
  log->count++;
}

static void fail(const char *property, const EditLog *log, const Text *text) {
  fprintf(stderr, "Property broken: %s\nEdits (\\n is a line break):\n", property);
  for (uint32_t i = 0; i < log->count; i++) fprintf(stderr, "  %s\n", log->lines[i]);
  fprintf(stderr, "Edited text (%u bytes):\n%.*s\n", text->length, (int)text->length, text->data);
  abort();
}

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size) {
  if (size > MAX_INPUT) return 0;

  TSParser *parser = ts_parser_new();
  if (!ts_parser_set_language(parser, tree_sitter_daml())) abort();

  Text text = {copy_bytes((const char *)data, (uint32_t)size), (uint32_t)size};
  TSTree *original = ts_parser_parse_string(parser, NULL, text.data, text.length);
  bool original_clean = !ts_node_has_error(ts_tree_root_node(original));

  TSTree *tree = ts_tree_copy(original);
  uint64_t state = hash(data, size);
  uint32_t count = 1 + below(&state, MAX_EDITS);
  Edit undo[MAX_EDITS];
  EditLog log = {.count = 0};
  for (uint32_t i = 0; i < count; i++) {
    Edit edit = random_edit(&text, &state);
    undo[i] = inverse(&text, &edit);
    log_edit(&log, &text, &edit);
    apply(&text, tree, &edit);
    free(edit.inserted);
  }

  TSTree *incremental = ts_parser_parse_string(parser, tree, text.data, text.length);
  ts_tree_delete(tree);
  TSTree *fresh = ts_parser_parse_string(parser, NULL, text.data, text.length);
  if (!ts_node_has_error(ts_tree_root_node(fresh)) &&
      !same_tree(ts_tree_root_node(incremental), ts_tree_root_node(fresh), &text)) {
    fail("the incremental parse of text without errors differs from a parse from scratch", &log, &text);
  }
  ts_tree_delete(fresh);

  for (uint32_t i = count; i-- > 0;) {
    apply(&text, incremental, &undo[i]);
    free(undo[i].inserted);
  }
  if (text.length != size || memcmp(text.data, data, size) != 0) {
    fail("undoing the edits doesn't give the input back (the harness is wrong)", &log, &text);
  }
  TSTree *restored = ts_parser_parse_string(parser, incremental, text.data, text.length);
  if (original_clean && !same_tree(ts_tree_root_node(restored), ts_tree_root_node(original), &text)) {
    fail("undoing the edits doesn't give the tree of the input back", &log, &text);
  }

  ts_tree_delete(restored);
  ts_tree_delete(incremental);
  ts_tree_delete(original);
  ts_parser_delete(parser);
  free(text.data);
  return 0;
}
