using System.ComponentModel.Composition;
using System.Diagnostics.CodeAnalysis;
using Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Classification;

// Every Daml classification derives from a predefined one, so it picks up the theme's colour for
// keywords, comments and so on until the user changes it under Fonts and Colors.
[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "MEF export fields.")]
[SuppressMessage("Usage", "CA2211:Non-constant fields should not be visible", Justification = "MEF export fields.")]
internal static class DamlClassificationDefinitions
{
    [Export]
    [Name(DamlClassificationNames.Keyword)]
    [BaseDefinition(PredefinedClassificationTypeNames.Keyword)]
    internal static ClassificationTypeDefinition? Keyword;

    [Export]
    [Name(DamlClassificationNames.Declaration)]
    [BaseDefinition(PredefinedClassificationTypeNames.Keyword)]
    internal static ClassificationTypeDefinition? Declaration;

    [Export]
    [Name(DamlClassificationNames.ContractKeyword)]
    [BaseDefinition(PredefinedClassificationTypeNames.Keyword)]
    internal static ClassificationTypeDefinition? ContractKeyword;

    // "type" is predefined by the editor, so it always exists; Roslyn's "class name" would give
    // types the C# colour but is only registered when the C# tooling is installed.
    [Export]
    [Name(DamlClassificationNames.Type)]
    [BaseDefinition(PredefinedClassificationTypeNames.Type)]
    internal static ClassificationTypeDefinition? Type;

    [Export]
    [Name(DamlClassificationNames.Comment)]
    [BaseDefinition(PredefinedClassificationTypeNames.Comment)]
    internal static ClassificationTypeDefinition? Comment;

    [Export]
    [Name(DamlClassificationNames.DocComment)]
    [BaseDefinition(PredefinedClassificationTypeNames.Comment)]
    internal static ClassificationTypeDefinition? DocComment;

    [Export]
    [Name(DamlClassificationNames.Pragma)]
    [BaseDefinition(PredefinedClassificationTypeNames.PreprocessorKeyword)]
    internal static ClassificationTypeDefinition? Pragma;

    [Export]
    [Name(DamlClassificationNames.StringLiteral)]
    [BaseDefinition(PredefinedClassificationTypeNames.String)]
    internal static ClassificationTypeDefinition? StringLiteral;

    [Export]
    [Name(DamlClassificationNames.Number)]
    [BaseDefinition(PredefinedClassificationTypeNames.Number)]
    internal static ClassificationTypeDefinition? Number;

    [Export]
    [Name(DamlClassificationNames.Operator)]
    [BaseDefinition(PredefinedClassificationTypeNames.Operator)]
    internal static ClassificationTypeDefinition? Operator;

    [Export]
    [Name(DamlClassificationNames.Identifier)]
    [BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
    internal static ClassificationTypeDefinition? Identifier;
}

// The entries under Tools > Options > Fonts and Colors. No colours are set here: each one shows
// its base classification's colour for the current theme until the user picks another.

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = DamlClassificationNames.Keyword)]
[Name(DamlClassificationNames.Keyword)]
[UserVisible(true)]
[Order(Before = Priority.Default)]
internal sealed class DamlKeywordFormat : ClassificationFormatDefinition
{
    public DamlKeywordFormat() => DisplayName = "Daml - Keyword";
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = DamlClassificationNames.Declaration)]
[Name(DamlClassificationNames.Declaration)]
[UserVisible(true)]
[Order(Before = Priority.Default)]
internal sealed class DamlDeclarationFormat : ClassificationFormatDefinition
{
    public DamlDeclarationFormat() => DisplayName = "Daml - Declaration keyword";
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = DamlClassificationNames.ContractKeyword)]
[Name(DamlClassificationNames.ContractKeyword)]
[UserVisible(true)]
[Order(Before = Priority.Default)]
internal sealed class DamlContractKeywordFormat : ClassificationFormatDefinition
{
    public DamlContractKeywordFormat() => DisplayName = "Daml - Contract keyword";
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = DamlClassificationNames.Type)]
[Name(DamlClassificationNames.Type)]
[UserVisible(true)]
[Order(Before = Priority.Default)]
internal sealed class DamlTypeFormat : ClassificationFormatDefinition
{
    public DamlTypeFormat() => DisplayName = "Daml - Type";
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = DamlClassificationNames.Comment)]
[Name(DamlClassificationNames.Comment)]
[UserVisible(true)]
[Order(Before = Priority.Default)]
internal sealed class DamlCommentFormat : ClassificationFormatDefinition
{
    public DamlCommentFormat() => DisplayName = "Daml - Comment";
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = DamlClassificationNames.DocComment)]
[Name(DamlClassificationNames.DocComment)]
[UserVisible(true)]
[Order(Before = Priority.Default)]
internal sealed class DamlDocCommentFormat : ClassificationFormatDefinition
{
    public DamlDocCommentFormat() => DisplayName = "Daml - Doc comment";
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = DamlClassificationNames.Pragma)]
[Name(DamlClassificationNames.Pragma)]
[UserVisible(true)]
[Order(Before = Priority.Default)]
internal sealed class DamlPragmaFormat : ClassificationFormatDefinition
{
    public DamlPragmaFormat() => DisplayName = "Daml - Pragma";
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = DamlClassificationNames.StringLiteral)]
[Name(DamlClassificationNames.StringLiteral)]
[UserVisible(true)]
[Order(Before = Priority.Default)]
internal sealed class DamlStringFormat : ClassificationFormatDefinition
{
    public DamlStringFormat() => DisplayName = "Daml - String";
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = DamlClassificationNames.Number)]
[Name(DamlClassificationNames.Number)]
[UserVisible(true)]
[Order(Before = Priority.Default)]
internal sealed class DamlNumberFormat : ClassificationFormatDefinition
{
    public DamlNumberFormat() => DisplayName = "Daml - Number";
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = DamlClassificationNames.Operator)]
[Name(DamlClassificationNames.Operator)]
[UserVisible(true)]
[Order(Before = Priority.Default)]
internal sealed class DamlOperatorFormat : ClassificationFormatDefinition
{
    public DamlOperatorFormat() => DisplayName = "Daml - Operator";
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = DamlClassificationNames.Identifier)]
[Name(DamlClassificationNames.Identifier)]
[UserVisible(true)]
[Order(Before = Priority.Default)]
internal sealed class DamlIdentifierFormat : ClassificationFormatDefinition
{
    public DamlIdentifierFormat() => DisplayName = "Daml - Identifier";
}
