; Generated from queries/highlights.scm by grammar/scripts/editor-queries.mjs: edit that file and run
; the script with --update.
; Captures renamed to Helix theme scopes; `@spell` and the patterns that need predicate-only
; captures (`@_op`, `@_name`) left out, since Helix would take those nodes' highlight away.

; Highlights for Daml.
;
; When several patterns capture the same node, the last one wins, as in the tree-sitter CLI,
; Neovim, Helix (25.07 and later) and Zed; general patterns come first and Daml's own at the end.
; Helix's copy, with its own capture names, is generated from this one.

; ----------------------------------------------------------------------------
; Parameters and variables
; NOTE: These are at the top, so that they have low priority,
; and don't override destructured parameters
(variable) @variable

(pattern/wildcard) @variable

(decl/function
  patterns: (patterns
    (_) @variable.parameter))

; Daml: each parameter, as for functions below; upstream captured the whole `patterns` node, which
; left the parameters themselves (and so their uses, through locals.scm) plain variables.
(expression/lambda
  patterns: (patterns
    (_) @variable.parameter))

(decl/function
  (infix
    (pattern) @variable.parameter))

; ----------------------------------------------------------------------------
; Literals and comments
(integer) @constant.numeric.integer

(negation) @constant.numeric.integer

(expression/literal
  (float)) @constant.numeric.float

(char) @constant.character

(string) @string

(unit) @string.special.symbol ; unit, as in ()

(comment) @comment

((haddock) @comment.documentation)

; ----------------------------------------------------------------------------
; Punctuation
[
  "("
  ")"
  "{"
  "}"
  "["
  "]"
] @punctuation.bracket

[
  ","
  ";"
] @punctuation.delimiter

; ----------------------------------------------------------------------------
; Keywords, operators, includes
[
  "forall"
  ; "∀" ; utf-8 is not cross-platform safe
] @keyword.control.repeat

(pragma) @keyword.directive

[
  "if"
  "then"
  "else"
  "case"
  "cases"
  "of"
] @keyword.control.conditional

[
  "import"
  "qualified"
  "module"
] @keyword.control.import

[
  (operator)
  (constructor_operator)
  (all_names)
  (wildcard)
  "."
  ".."
  "="
  "|"
  ":"
  "=>"
  "->"
  "<-"
  "\\"
  "`"
  "@"
] @operator

; TODO broken, also huh?
; ((qualified_module
;   (module) @constructor)
;   .
;   (module))

(module
  (module_id) @namespace)

; Daml: keywords that declare something, as the Visual Studio extension colours them.
[
  "let"
  "in"
  "class"
  "instance"
  "data"
  "newtype"
  "type"
  "deriving"
] @keyword.storage.type

[
  "where"
  "pattern"
  "family"
  "as"
  "hiding"
  "via"
  "stock"
  "anyclass"
  "do"
  "mdo"
  "rec"
  "infix"
  "infixl"
  "infixr"
] @keyword

; ----------------------------------------------------------------------------
; Functions and variables
[
  (decl/function name: (variable) @function)
  (decl/bind name: (variable) @function)
  (decl/signature name: (variable) @function)
  (decl/signature names: (binding_list (variable) @function))
]

(decl/bind
  name: (variable) @variable)

; Consider signatures (and accompanying functions)
; with only one value on the rhs as variables
(decl/signature
  name: (variable) @variable
  type: (type))

((decl/signature) @function
  .
  (decl/function
    name: (variable) @function))

(decl/bind
  name: (variable) @function
  (match
    expression: (expression/lambda)))

; view patterns
(view_pattern
  [
    (expression/variable) @function.call
    (expression/qualified
      (variable) @function.call)
  ])

; consider infix functions as operators
(infix_id
  [
    (variable) @operator
    (qualified
      (variable) @operator)
  ])

; decl/function calls with an infix operator
; e.g. func <$> a <*> b
(infix
  [
    (variable) @function.call
    (qualified
      ((module) @namespace
        (variable) @function.call))
  ]
  .
  (operator))

; infix operators applied to variables
((expression/variable) @variable
  .
  (operator))

((operator)
  .
  [
    (expression/variable) @variable
    (expression/qualified
      (variable) @variable)
  ])

(apply
  [
    (expression/variable) @function.call
    (expression/qualified
      (variable) @function.call)
  ])

; function compositions, in parentheses, applied
; lhs
(apply
  .
  (expression/parens
    (infix
      [
        (variable) @function.call
        (qualified
          (variable) @function.call)
      ]
      .
      (operator))))

; rhs
(apply
  .
  (expression/parens
    (infix
      (operator)
      .
      [
        (variable) @function.call
        (qualified
          (variable) @function.call)
      ])))

; variables being passed to a function call
(apply
  (_)
  .
  [
    (expression/variable) @variable
    (expression/qualified
      (variable) @variable)
  ])

; main is always a function
; (this prevents `main = undefined` from being highlighted as a variable)
(decl/bind
  name: (variable) @function
  (#eq? @function "main"))

; scoped function types (func :: a -> b)
(signature
  pattern: (pattern/variable) @function
  type: (quantified_type))

; signatures that have a function type
; + binds that follow them
(decl/signature
  name: (variable) @function
  type: (quantified_type))

; ----------------------------------------------------------------------------
; Types
(name) @type

(type/star) @type

; Daml: only type variables; every variable here would override all the patterns above.
(type/variable) @type

(constructor) @constructor

; True or False
((constructor) @constant.builtin.boolean
  (#any-of? @constant.builtin.boolean "True" "False"))

; otherwise (= True)
((variable) @constant.builtin.boolean
  (#eq? @constant.builtin.boolean "otherwise"))

; ----------------------------------------------------------------------------
; Quasi-quotes
(quoter) @function.call

; namespaced quasi-quoter
(quasiquote
  (_
    (module) @namespace
    .
    (variable) @function.call))

; Highlighting of quasiquote_body for other languages is handled by injections.scm
; ----------------------------------------------------------------------------
; Exceptions/error handling
((variable) @keyword.control.exception
  (#any-of? @keyword.control.exception
    "error" "undefined" "try" "tryJust" "tryAny" "catch" "catches" "catchJust" "handle" "handleJust"
    "throw" "throwIO" "throwTo" "throwError" "ioError" "mask" "mask_" "uninterruptibleMask"
    "uninterruptibleMask_" "bracket" "bracket_" "bracketOnErrorSource" "finally" "fail"
    "onException" "expectationFailure" "abort" "assert" "assertMsg" "failWithStatus"))

; ----------------------------------------------------------------------------
; Debugging
((variable) @keyword.debug
  (#any-of? @keyword.debug
    "trace" "traceId" "traceShow" "traceShowId" "traceWith" "traceShowWith" "traceStack" "traceIO"
    "traceM" "traceShowM" "traceEvent" "traceEventWith" "traceEventIO" "flushEventLog" "traceMarker"
    "traceMarkerIO" "debug" "debugRaw"))

; ----------------------------------------------------------------------------
; Fields

(field_name
  (variable) @variable.other.member)

(import_name
  (name)
  .
  (children
    (variable) @variable.other.member))

; ----------------------------------------------------------------------------
; Daml
; Templates, interfaces and exceptions declare things; the contract keywords
; give their terms. Last, so that they win.

[
  "template"
  "interface"
  "exception"
] @keyword.storage.type

[
  "with"
  "choice"
  "controller"
  "signatory"
  "observer"
  "ensure"
  "key"
  "maintainer"
  "message"
  "viewtype"
  "requires"
  "for"
] @keyword.contract

(choice_modifier) @keyword.contract

[
  "try"
  "catch"
] @keyword.control.exception

; A choice and an interface method are operations on a contract.
(choice_declaration
  name: (name) @function.method)

(interface_body
  (signature
    name: (variable) @function.method))

; The view and methods an interface instance implements.
(interface_instance
  body: (instance_declarations
    [
      (bind name: (variable) @function.method)
      (function name: (variable) @function.method)
    ]))
