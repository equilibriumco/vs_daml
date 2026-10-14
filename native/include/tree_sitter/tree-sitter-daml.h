#ifndef TREE_SITTER_DAML_H_
#define TREE_SITTER_DAML_H_

typedef struct TSLanguage TSLanguage;

#ifdef __cplusplus
extern "C" {
#endif

// The Daml language for ts_parser_set_language and ts_query_new, from daml_ts.dll.
const TSLanguage *tree_sitter_daml(void);

#ifdef __cplusplus
}
#endif

#endif  // TREE_SITTER_DAML_H_
