; Indentation, written by hand for Zed: a new line inside one of these blocks goes one level deeper
; than the line the block starts on, as queries/indents.scm does it.

[
  (template_declaration)
  (interface_declaration)
  (exception_declaration)
  (choice_declaration)
  (interface_instance)
  (class)
  (instance)
  (data_type)
  (function name: (_))
  (bind)
  (do)
  (expression/case)
  (expression/let_in)
  (expression/conditional)
  (expression/lambda)
  (expression/record)
] @indent

(_ "[" "]" @end) @indent

(_ "{" "}" @end) @indent

(_ "(" ")" @end) @indent
