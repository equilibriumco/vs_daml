; The outline panel and symbol search: templates with their choices, interfaces with their
; methods and choices, instances, exceptions, types, classes and top-level signatures. Written by
; hand for Zed.

(template_declaration
  "template" @context
  name: (_) @name) @item

(choice_declaration
  "choice" @context
  name: (_) @name) @item

(interface_declaration
  "interface" @context
  name: (_) @name) @item

(interface_body
  (signature
    name: (_) @name) @item)

(interface_instance
  "interface" @context
  "instance" @context
  interface: (_) @name
  "for" @context
  for: (_) @name) @item

(exception_declaration
  "exception" @context
  name: (_) @name) @item

(data_type
  "data" @context
  name: (_) @name) @item

(newtype
  "newtype" @context
  name: (_) @name) @item

(type_synonym
  "type" @context
  name: (_) @name) @item

(class
  "class" @context
  name: (_) @name) @item

(class_declarations
  (signature
    name: (_) @name) @item)

(declarations
  (signature
    name: (_) @name) @item)
