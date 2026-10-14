// Smoke test of daml_ts.dll through its exports: the runtime takes the language, a contract
// template parses into the nodes the queries rely on, an edit parses incrementally, a query finds
// what the edit changed, and what the API allocates for the caller can be freed by the caller.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <tree_sitter/api.h>

__declspec(dllimport) const TSLanguage *tree_sitter_daml(void);

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

int main(void) {
  const TSLanguage *language = tree_sitter_daml();
  uint32_t abi = ts_language_abi_version(language);
  check(abi >= TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION && abi <= TREE_SITTER_LANGUAGE_VERSION,
        "the runtime accepts the language's ABI version");
  const TSLanguageMetadata *version = ts_language_metadata(language);
  check(version != NULL, "the language carries the grammar's version");

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
  ts_parser_delete(parser);

  if (version != NULL) {
    printf("daml_ts: grammar %u.%u.%u, language ABI %u\n", version->major_version,
           version->minor_version, version->patch_version, abi);
  }
  printf("%s\n", failures == 0 ? "smoke test passed" : "smoke test FAILED");
  return failures == 0 ? 0 : 1;
}
