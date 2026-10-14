; Scopes, definitions and references for Helix, written by hand: Helix colours a reference with the
; scope its definition's capture names (local.definition.<scope>). Same nodes as queries/locals.scm.

[
  (function)
  (expression/lambda)
  (expression/let_in)
  (alternative)
  (expression/do)
  (template_declaration)
  (choice_declaration)
  (exception_declaration)
] @local.scope

; Parameters and the names a pattern binds, in equations, lambdas, case alternatives and `<-`.
(pattern/variable) @local.definition.variable.parameter

; `let` and `where` bindings.
(local_binds
  (bind
    name: (variable) @local.definition.variable))

(local_binds
  (function
    name: (variable) @local.definition.function))

; The fields after `with`: a template's fields and a choice's parameters, in scope in its body.
(with_fields
  (field
    name: (field_name
      (variable) @local.definition.variable.other.member)))

(expression/variable) @local.reference
