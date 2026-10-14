; Indentation in the style of nvim-treesitter: the lines inside a captured block go one level
; deeper than the line it starts on. Best effort for a layout language: a level is counted once
; for each block that starts on an earlier line, so a template's fields, two levels in (`with` is
; part of the template, not of the fields' block), come out one level in.

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
] @indent.begin

[
  (do)
  (expression/case)
  (expression/let_in)
  (expression/conditional)
  (expression/lambda)
  (expression/record)
] @indent.begin

; Comments keep the indentation they were written with.
[
  (comment)
  (haddock)
] @indent.auto
