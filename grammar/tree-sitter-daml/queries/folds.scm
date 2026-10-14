; Folds: the imports, declarations and their blocks, and long comments. Editors fold only what
; spans more than one line.

(imports) @fold

[
  (template_declaration)
  (interface_declaration)
  (exception_declaration)
  (choice_declaration)
  (interface_instance)
  (data_type)
  (newtype)
  (class)
  (instance)
  (function name: (_))
  (bind)
] @fold

[
  (do)
  (expression/case)
  (expression/let_in)
] @fold

[
  (comment)
  (haddock)
] @fold
