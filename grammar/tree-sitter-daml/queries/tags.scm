; Tags for code navigation: what a file defines, and the functions it calls.
; Templates, interfaces, exceptions and data types are classes; choices and methods are methods.
; Top-level functions and values are tagged by their signatures: one tag for each name, however
; many equations define it.

(header
  module: (module) @name) @definition.module

(template_declaration
  name: (name) @name) @definition.class

(interface_declaration
  name: (name) @name) @definition.class

(exception_declaration
  name: (name) @name) @definition.class

(data_type
  name: (name) @name) @definition.class

(newtype
  name: (name) @name) @definition.class

(type_synonym
  name: (name) @name) @definition.type

(class
  name: (name) @name) @definition.interface

(choice_declaration
  name: (name) @name) @definition.method

(interface_body
  (signature
    name: (variable) @name) @definition.method)

(class_declarations
  (signature
    name: (variable) @name) @definition.method)

(declarations
  (signature
    name: (variable) @name) @definition.function)

(declarations
  (signature
    names: (binding_list
      (variable) @name)) @definition.function)

(apply
  function: (expression/variable) @name) @reference.call
