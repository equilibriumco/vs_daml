; Scopes, definitions and references, for editors that colour a name like its definition or find
; its other uses. Good enough for that, but not a basis for safe renaming: a `where` of a binding,
; for one, isn't a scope of its own.

; ----------------------------------------------------------------------------
; Scopes
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

; ----------------------------------------------------------------------------
; Definitions

; Parameters and the names a pattern binds, in equations, lambdas, case alternatives and `<-`.
(pattern/variable) @local.definition

; `let` and `where` bindings.
(local_binds
  (bind
    name: (variable) @local.definition))

(local_binds
  (function
    name: (variable) @local.definition))

; The fields after `with`: a template's fields and a choice's parameters, in scope in its body.
(with_fields
  (field
    name: (field_name
      (variable) @local.definition)))

; ----------------------------------------------------------------------------
; References
(expression/variable) @local.reference
