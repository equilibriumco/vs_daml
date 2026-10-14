// The constructs Daml adds to Haskell: templates and their clauses, and the `with` blocks of
// fields they share with records and choices. Kept apart from the modules inherited from
// tree-sitter-haskell, so that taking upstream changes stays a matter of merging those.

const {
  sep1,
  layout,
} = require('./util.js')

module.exports = {

  // ------------------------------------------------------------------------
  // with
  // ------------------------------------------------------------------------

  /**
   * The fields after `with`: one per line, or several on a line separated by layout semicolons,
   * `name : Type` each (`a, b : Party` declares two). Templates, choices and records use it.
   */
  with_fields: $ => layout($, field('field', $.field)),

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
  ),

  /**
   * `signatory a, b`: each expression is a party, a list or a set of them.
   */
  signatory_clause: $ => seq('signatory', sep1(',', field('party', $.expression))),

  observer_clause: $ => seq('observer', sep1(',', field('party', $.expression))),

  ensure_clause: $ => seq('ensure', field('condition', $.expression)),

}
