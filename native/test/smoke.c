// Smoke test of daml_ts.dll through its exports: the runtime takes the language, a contract
// template parses into the nodes the queries rely on, an edit parses incrementally, a query finds
// what the edit changed, and what the API allocates for the caller can be freed by the caller.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <tree_sitter/api.h>
#include <tree_sitter/tree-sitter-daml.h>

static const char source[] =
    "module Main where\n"
    "\n"
    "template Iou\n"
    "  with\n"
    "    issuer : Party\n"
    "    owner : Party\n"
    "  where\n"
    "    signatory issuer\n"
    "    observer owner\n"
    "\n"
    "    choice Transfer : ContractId Iou\n"
    "      with\n"
    "        newOwner : Party\n"
    "      controller owner\n"
    "      do create this with owner = newOwner\n";

static int failures = 0;

static void check(bool condition, const char *what) {
  if (!condition) {
    fprintf(stderr, "FAILED: %s\n", what);
    failures++;
  }
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

static void edit_text(TSTree *tree, char *text, uint32_t start, uint32_t removed,
                      const char *inserted, uint32_t added) {
  TSInputEdit edit = {start, start + removed, start + added,
                      point_at(text, start), point_at(text, start + removed), {0, 0}};
  memmove(text + start + added, text + start + removed, strlen(text + start + removed) + 1);
  memcpy(text + start, inserted, added);
  edit.new_end_point = point_at(text, start + added);
  ts_tree_edit(tree, &edit);
}

static void check_layout_undo(TSParser *parser) {
  const char original[] = "\nf =\n  let g : Int\n      g = 1\n  in g\n\n";
  char text[sizeof original];
  memcpy(text, original, sizeof original);
  TSTree *tree = ts_parser_parse_string(parser, NULL, text, (uint32_t)strlen(text));
  char *expected = ts_node_string(ts_tree_root_node(tree));

  // Two edits before a reparse, then one undo batch. The layout opener changes columns.
  edit_text(tree, text, 19, 6, "", 0);
  edit_text(tree, text, 5, 1, "", 0);
  TSTree *changed = ts_parser_parse_string(parser, tree, text, (uint32_t)strlen(text));
  edit_text(changed, text, 5, 0, original + 5, 1);
  edit_text(changed, text, 19, 0, original + 19, 6);
  TSTree *restored = ts_parser_parse_string(parser, changed, text, (uint32_t)strlen(text));
  char *actual = ts_node_string(ts_tree_root_node(restored));
  check(strcmp(text, original) == 0 && strcmp(actual, expected) == 0,
        "undoing batched indentation edits restores the original layout");
  free(expected);
  free(actual);
  ts_tree_delete(restored);
  ts_tree_delete(changed);
  ts_tree_delete(tree);
}

static void check_bind_suffix_undo(TSParser *parser) {
  const char original[] = "f = do\n  (-1) <- action\n  pure ()\n";
  char text[sizeof original];
  memcpy(text, original, sizeof original);
  uint32_t start = (uint32_t)(strstr(original, "<-") - original);
  uint32_t length = (uint32_t)strlen(original);
  TSTree *tree = ts_parser_parse_string(parser, NULL, text, length);
  char *expected = ts_node_string(ts_tree_root_node(tree));
  edit_text(tree, text, start, length - start, "", 0);
  TSTree *truncated = ts_parser_parse_string(parser, tree, text, start);
  edit_text(truncated, text, start, 0, original + start, length - start);
  TSTree *restored = ts_parser_parse_string(parser, truncated, text, length);
  char *actual = ts_node_string(ts_tree_root_node(restored));
  check(strcmp(actual, expected) == 0, "restoring a bind suffix reparses the preceding parentheses");
  free(expected);
  free(actual);
  ts_tree_delete(restored);
  ts_tree_delete(truncated);
  ts_tree_delete(tree);
}

// An error at the top of the file leaves the layout scanner in a state that puts the final #endif
// inside the instance body. Undoing the error moves it back out: both trees are right for their
// text, and the changed ranges between them must cover the #endif line. The runtime we build is
// patched for this; its upstream version declared the enclosing declarations identical.
static void check_changed_ranges_after_recovery(TSParser *parser) {
  const char original[] = "\ninstance A where\n  a = a\n\n#if\n  a = a\n#endif\n";
  char text[sizeof original + 1];
  memcpy(text, original, sizeof original);
  TSTree *tree = ts_parser_parse_string(parser, NULL, text, (uint32_t)strlen(text));
  char *expected = ts_node_string(ts_tree_root_node(tree));
  edit_text(tree, text, 0, 0, ">", 1);
  TSTree *broken = ts_parser_parse_string(parser, tree, text, (uint32_t)strlen(text));
  edit_text(broken, text, 0, 1, "", 0);
  TSTree *restored = ts_parser_parse_string(parser, broken, text, (uint32_t)strlen(text));
  char *actual = ts_node_string(ts_tree_root_node(restored));
  check(strcmp(actual, expected) == 0, "undoing an error at the top restores the original tree");

  uint32_t endif_start = (uint32_t)(strstr(original, "#endif") - original);
  uint32_t endif_end = endif_start + (uint32_t)strlen("#endif");
  uint32_t count = 0;
  TSRange *ranges = ts_tree_get_changed_ranges(broken, restored, &count);
  bool covered = false;
  for (uint32_t i = 0; i < count; i++) {
    if (ranges[i].start_byte <= endif_start && endif_end <= ranges[i].end_byte) covered = true;
  }
  check(covered, "the changed ranges after undoing the error cover the #endif that moved");
  free(ranges);
  free(expected);
  free(actual);
  ts_tree_delete(restored);
  ts_tree_delete(broken);
  ts_tree_delete(tree);
}

int main(void) {
  const TSLanguage *language = tree_sitter_daml();
  uint32_t abi = ts_language_abi_version(language);
  check(abi >= TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION && abi <= TREE_SITTER_LANGUAGE_VERSION,
        "the runtime accepts the language's ABI version");
  // A parser generated before tree-sitter.json last changed would carry another version.
  const TSLanguageMetadata *version = ts_language_metadata(language);
  check(version != NULL && version->major_version == DAML_TS_VERSION_MAJOR &&
            version->minor_version == DAML_TS_VERSION_MINOR &&
            version->patch_version == DAML_TS_VERSION_PATCH,
        "the language carries the grammar's version from tree-sitter.json");

  TSParser *parser = ts_parser_new();
  check(ts_parser_set_language(parser, language), "the parser takes the language");
  TSTree *tree = ts_parser_parse_string(parser, NULL, source, (uint32_t)strlen(source));
  check(!ts_node_has_error(ts_tree_root_node(tree)), "the template parses without errors");

  // The API's own allocation, released with the C runtime's free() as its documentation says.
  char *sexp = ts_node_string(ts_tree_root_node(tree));
  check(strstr(sexp, "(template_declaration") != NULL, "the tree has a template_declaration");
  check(strstr(sexp, "(choice_declaration") != NULL, "the tree has a choice_declaration");
  free(sexp);

  char edited[sizeof source];
  memcpy(edited, source, sizeof source);
  char *name = strstr(edited, "Transfer");
  memcpy(name, "Transmit", 8);
  uint32_t start = (uint32_t)(name - edited);
  TSInputEdit edit = {start, start + 8, start + 8,
                      point_at(edited, start), point_at(edited, start + 8), point_at(edited, start + 8)};
  ts_tree_edit(tree, &edit);
  TSTree *reparsed = ts_parser_parse_string(parser, tree, edited, (uint32_t)strlen(edited));
  check(!ts_node_has_error(ts_tree_root_node(reparsed)), "the edited template parses without errors");

  const char pattern[] = "(choice_declaration name: (_) @name)";
  uint32_t error_offset;
  TSQueryError error;
  TSQuery *query = ts_query_new(language, pattern, (uint32_t)strlen(pattern), &error_offset, &error);
  check(query != NULL, "the query compiles");
  bool found = false;
  if (query != NULL) {
    TSQueryCursor *cursor = ts_query_cursor_new();
    ts_query_cursor_exec(cursor, query, ts_tree_root_node(reparsed));
    TSQueryMatch match;
    if (ts_query_cursor_next_match(cursor, &match) && match.capture_count == 1) {
      uint32_t from = ts_node_start_byte(match.captures[0].node);
      uint32_t to = ts_node_end_byte(match.captures[0].node);
      found = to - from == 8 && memcmp(edited + from, "Transmit", 8) == 0;
    }
    ts_query_cursor_delete(cursor);
    ts_query_delete(query);
  }
  check(found, "the query finds the renamed choice");

  ts_tree_delete(reparsed);
  ts_tree_delete(tree);
  check_layout_undo(parser);
  check_bind_suffix_undo(parser);
  check_changed_ranges_after_recovery(parser);
  ts_parser_delete(parser);

  if (version != NULL) {
    printf("daml_ts: grammar %u.%u.%u, language ABI %u\n", version->major_version,
           version->minor_version, version->patch_version, abi);
  }
  printf("%s\n", failures == 0 ? "smoke test passed" : "smoke test FAILED");
  return failures == 0 ? 0 : 1;
}
