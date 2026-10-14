; Indentation for Helix, written by hand (its captures aren't nvim-treesitter's): a line inside one
; of these blocks goes one level deeper than the line the block starts on, as queries/indents.scm
; does it. No `#set! scope`, which Helix accepts differently since 25.07.

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
