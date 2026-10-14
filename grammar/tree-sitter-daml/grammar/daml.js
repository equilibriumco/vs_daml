// The constructs Daml adds to Haskell: templates and their clauses, choices, and the `with`
// blocks of fields they share with records. Kept apart from the modules inherited from
// tree-sitter-haskell, so that taking upstream changes stays a matter of merging those.

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
   * `V1 with payload -> …`: a record pattern, as with braces.
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
    $.choice_declaration,
  ),

  /**
   * `signatory a, b`: each expression is a party, a list or a set of them.
   */
  signatory_clause: $ => seq('signatory', sep1(',', field('party', $.expression))),

  observer_clause: $ => seq('observer', sep1(',', field('party', $.expression))),

  ensure_clause: $ => seq('ensure', field('condition', $.expression)),

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
    field('body', alias($._exp_do, $.do)),
  ),

  choice_modifier: _ => choice('nonconsuming', 'preconsuming', 'postconsuming'),

  /**
   * `controller a, b`: who may exercise the choice. The `do` after it starts the body, it isn't an
   * argument of the last party.
   */
  controller_clause: $ => seq('controller', sep1(',', field('party', $._controller_party))),

  _controller_party: $ => prec('daml-clause', $.expression),

}
