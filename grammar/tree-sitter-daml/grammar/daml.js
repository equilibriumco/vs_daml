// The constructs Daml adds to Haskell: templates and their clauses, choices, interfaces and their
// instances, exceptions, and the `with` blocks of fields they share with records. Kept apart from
// the modules inherited from tree-sitter-haskell, so that taking upstream changes stays a matter of
// merging those.

const {
  sep1,
  layout,
  layout_sort,
} = require('./util.js')

/**
 * The block after `with`. The scanner knows it apart from other blocks: inside parentheses, its
 * commas and `=` don't end it as they would end other blocks, and an arrow that can't continue it
 * does.
 */
const with_layout = ($, rule) => layout_sort($, $._cmd_layout_start_with, rule)

module.exports = {

  // ------------------------------------------------------------------------
  // with
  // ------------------------------------------------------------------------

  /**
   * The fields after `with`: one per line, or several on a line separated by semicolons or commas,
   * `name : Type` each (`a, b : Party` declares two). Templates and choices use it.
   */
  with_fields: $ => with_layout($, sep1(',', field('field', $.field))),

  /**
   * `data P = P with x : Int`: the same record as `P {x : Int}`, so the node is the same too.
   */
  _record_fields_with: $ => seq('with', with_layout($, sep1(',', field('field', $.field)))),

  /**
   * `T with f = v`, `x with f = v`: record construction and update, as with braces. Fields are
   * separated by semicolons or commas, `with a; b, c = 1`, and may be puns or `..`.
   */
  _exp_record_with: $ => seq('with', with_layout($, sep1(',', field('field', $.field_update)))),

  /**
   * `V1 with payload -> …`: a record pattern, as with braces. Before the `=` of a function head,
   * `f Con with a; .. = …`, the block ends because that `=` can't give the `..` a value.
   */
  _pat_record_with: $ => seq('with', with_layout($, sep1(',', field('field', $.field_pattern)))),

  // ------------------------------------------------------------------------
  // template
  // ------------------------------------------------------------------------

  /**
   * `template Name with <fields> where <body>`. The `with` may end the header line or start the
   * next one.
   */
  template_declaration: $ => seq(
    'template',
    field('name', $.name),
    optional(seq('with', field('parameters', $.with_fields))),
    optional(seq($._where, optional(field('body', $.template_body)))),
  ),

  template_body: $ => layout($, $._template_body_item),

  _template_body_item: $ => choice(
    $.signatory_clause,
    $.observer_clause,
    $.ensure_clause,
    $.key_clause,
    $.maintainer_clause,
    $.choice_declaration,
    $.interface_instance,
  ),

  /**
   * `signatory a, b`: each expression is a party, a list or a set of them.
   */
  signatory_clause: $ => seq('signatory', sep1(',', field('party', $.expression))),

  observer_clause: $ => seq('observer', sep1(',', field('party', $.expression))),

  ensure_clause: $ => seq('ensure', field('condition', $.expression)),

  /**
   * `key (bank, number) : (Party, Text)`: the contract key and its type. Keys compile again from
   * SDK 3.6 (3.5 only with a development target).
   */
  key_clause: $ => seq('key', field('expression', $.expression), $._colon2, field('type', $.quantified_type)),

  /**
   * `maintainer key._1`: who maintains the key, computed from `key`.
   */
  maintainer_clause: $ => seq('maintainer', sep1(',', field('party', $.expression))),

  // ------------------------------------------------------------------------
  // choice
  // ------------------------------------------------------------------------

  /**
   * `choice Name : Type with <fields> observer … controller … do …`, in a template or an
   * interface. The parts after the type may each start a line of their own, indented more than
   * the choice; the type may too. A `with` may have no fields before the `controller`.
   */
  choice_declaration: $ => seq(
    optional(field('modifier', $.choice_modifier)),
    'choice',
    field('name', $.name),
    $._colon2,
    field('return_type', $.quantified_type),
    optional(seq('with', optional(field('parameters', $.with_fields)))),
    optional(field('observer', $.observer_clause)),
    field('controller', $.controller_clause),
    field('body', alias($._choice_body, $.do)),
  ),

  /**
   * The body, upstream's `do` node. The scanner puts a zero-width token before its `do`, so the
   * `do` is never taken for an argument of the controller's last expression.
   */
  _choice_body: $ => seq($._cmd_choice_body, 'do', $._statements),

  choice_modifier: _ => choice('nonconsuming', 'preconsuming', 'postconsuming'),

  /**
   * `controller a, b`: who may exercise the choice. The `do` after it starts the body, it isn't an
   * argument of the last party (see `_choice_body`).
   */
  controller_clause: $ => seq('controller', sep1(',', field('party', $.expression))),

  // ------------------------------------------------------------------------
  // interface
  // ------------------------------------------------------------------------

  /**
   * `interface Name requires A, B where <body>`. The names may be qualified.
   */
  interface_declaration: $ => seq(
    'interface',
    field('name', $.name),
    optional(field('requires', $.requires_clause)),
    optional(seq($._where, optional(field('body', $.interface_body)))),
  ),

  requires_clause: $ => seq('requires', sep1(',', field('interface', $._tyconids))),

  /**
   * The view type, the methods, written as signatures, and the choices.
   */
  interface_body: $ => layout($, $._interface_body_item),

  _interface_body_item: $ => choice(
    $.viewtype_clause,
    $.signature,
    $.choice_declaration,
  ),

  viewtype_clause: $ => seq('viewtype', field('type', $._tyconids)),

  /**
   * `interface instance I for T where <methods>`, in a template. The methods and `view` are
   * bindings, so the body is the same node as a class instance's.
   */
  interface_instance: $ => seq(
    'interface',
    'instance',
    field('interface', $._tyconids),
    'for',
    field('for', $._tyconids),
    optional(seq($._where, optional(field('body', $.instance_declarations)))),
  ),

  // ------------------------------------------------------------------------
  // exception
  // ------------------------------------------------------------------------

  /**
   * `exception Name with <fields> where message …`. Deprecated since SDK 3.3 in favour of
   * `failWithStatus`, but still compiled.
   */
  exception_declaration: $ => seq(
    'exception',
    field('name', $.name),
    optional(seq('with', field('parameters', $.with_fields))),
    optional(seq($._where, optional(field('body', $.exception_body)))),
  ),

  exception_body: $ => layout($, $.message_clause),

  message_clause: $ => seq('message', field('message', $.expression)),

  /**
   * `try <body> catch <alternatives>`, the alternatives as in a `case`. The `catch` may stand in
   * the column of the block around the `try`, so a layout semicolon may come before it.
   */
  _exp_try: $ => seq(
    'try',
    field('body', $._exp),
    optional($._cond_layout_semicolon),
    'catch',
    optional(field('alternatives', $.alternatives)),
  ),

}
