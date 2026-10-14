; Text objects for Helix (`maf`, `]c`, `mia` and the like), written by hand.

; Equations and bindings are the functions, their right-hand sides the inside; a choice is an
; operation on a contract, its `do` block the inside.
(function
  name: (_)
  match: (_) @function.inside) @function.around

(bind
  match: (_) @function.inside) @function.around

(choice_declaration
  body: (_) @function.inside) @function.around

; Templates, interfaces, exceptions, instances, data types and classes.
(template_declaration
  body: (_) @class.inside) @class.around

(interface_declaration
  body: (_) @class.inside) @class.around

(exception_declaration
  body: (_) @class.inside) @class.around

(interface_instance
  body: (_) @class.inside) @class.around

(data_type
  constructors: (_) @class.inside) @class.around

(class
  declarations: (_) @class.inside) @class.around

(instance
  declarations: (_) @class.inside) @class.around

; An equation's patterns, and the fields of a template or the parameters of a choice.
(patterns
  (_) @parameter.inside)

(with_fields
  (field) @parameter.inside)

[
  (comment)
  (haddock)
] @comment.inside

(comment)+ @comment.around

(haddock)+ @comment.around
