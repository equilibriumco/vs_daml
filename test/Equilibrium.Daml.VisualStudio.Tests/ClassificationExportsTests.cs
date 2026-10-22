using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Classification;
using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.Composition.Reflection;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Tests;

// The colouring parts as VS discovers them: the tagger for daml buffers, one classification
// type per Daml colour with a theme-aware base, and a Fonts and Colors entry for each.
public class ClassificationExportsTests
{
    public static TheoryData<string, string> ClassificationBases => new()
    {
        { DamlClassificationNames.Keyword, "keyword" },
        { DamlClassificationNames.Declaration, "keyword" },
        { DamlClassificationNames.ContractKeyword, "keyword" },
        { DamlClassificationNames.Type, "type" },
        { DamlClassificationNames.Comment, "comment" },
        { DamlClassificationNames.DocComment, "comment" },
        { DamlClassificationNames.Pragma, "preprocessor keyword" },
        { DamlClassificationNames.StringLiteral, "string" },
        { DamlClassificationNames.Number, "number" },
        { DamlClassificationNames.Operator, "operator" },
        { DamlClassificationNames.Identifier, "identifier" },
    };

    [Fact, Trait("Category", "Editor")]
    public async Task Daml_buffers_get_one_tagger_for_colours_and_one_for_folding()
    {
        var exports = (await MefParts.ExportsAsync(typeof(ITaggerProvider))).ToList();

        exports.SelectMany(e => TypeNames(e.Metadata["TagTypes"])).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe([typeof(IClassificationTag).FullName, typeof(IStructureTag).FullName]);
        exports.ShouldAllBe(e => ((IEnumerable<string>)e.Metadata["ContentTypes"]!).SequenceEqual(new[] { "daml" }));
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Daml_buffers_get_their_own_word_navigator()
    {
        var export = (await MefParts.ExportsAsync(typeof(Microsoft.VisualStudio.Text.Operations.ITextStructureNavigatorProvider))).Single();

        ((IEnumerable<string>)export.Metadata["ContentTypes"]!).ShouldBe(["daml"]);
    }

    [Theory, Trait("Category", "Editor")]
    [InlineData(typeof(Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.IAsyncCompletionSourceProvider))]
    [InlineData(typeof(Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.IAsyncCompletionCommitManagerProvider))]
    public async Task Daml_views_get_snippets_in_completion(Type contract)
    {
        var export = (await MefParts.ExportsAsync(contract)).Single();

        ((IEnumerable<string>)export.Metadata["ContentTypes"]!).ShouldBe(["daml"]);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Daml_views_get_markers_for_bracket_pairs_and_names()
    {
        var exports = (await MefParts.ExportsAsync(typeof(IViewTaggerProvider))).ToList();

        exports.Count.ShouldBe(2);
        exports.ShouldAllBe(e => ((IEnumerable<string>)e.Metadata["ContentTypes"]!).SequenceEqual(new[] { "daml" }));
        exports.ShouldAllBe(e => TypeNames(e.Metadata["TagTypes"]).SequenceEqual(new[] { typeof(TextMarkerTag).FullName }));
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Daml_views_get_smart_indent()
    {
        var export = (await MefParts.ExportsAsync(typeof(Microsoft.VisualStudio.Text.Editor.ISmartIndentProvider))).Single();

        ((IEnumerable<string>)export.Metadata["ContentTypes"]!).ShouldBe(["daml"]);
    }

    // The pairs come from the language configuration through the editor's own brace completion;
    // a second provider of ours would only compete with it. VS keeps brace completion switched
    // off for a language without an options page, so every editable Daml view gets the switch.
    [Fact, Trait("Category", "Editor")]
    public async Task Daml_exports_no_brace_pairs_of_its_own()
    {
        var exports = await MefParts.ExportsAsync(typeof(Microsoft.VisualStudio.Text.BraceCompletion.IBraceCompletionDefaultProvider));

        exports.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Brace_completion_is_switched_on_for_editable_daml_views()
    {
        var parts = await MefParts.DiscoverAsync();
        var contract = typeof(Microsoft.VisualStudio.Text.Editor.IWpfTextViewCreationListener);

        var definition = MefParts.PartsExporting(parts, contract).Single(p => p.Type.FullName == typeof(Editing.DamlBraceCompletionSwitch).FullName);

        var export = definition.ExportedTypes.Single(e => e.ContractName == MefParts.ContractName(contract));
        ((IEnumerable<string>)export.Metadata["ContentTypes"]!).ShouldBe(["daml"]);
        ((IEnumerable<string>)export.Metadata["TextViewRoles"]!).ShouldBe([Microsoft.VisualStudio.Text.Editor.PredefinedTextViewRoles.Editable]);
    }

    [Theory, Trait("Category", "Editor")]
    [MemberData(nameof(ClassificationBases))]
    public async Task Each_daml_classification_derives_from_a_predefined_one(string name, string baseType)
    {
        var exports = await MefParts.ExportsAsync(typeof(ClassificationTypeDefinition));

        var export = exports.Single(e => (string)e.Metadata["Name"]! == name);
        ((IEnumerable<string>)export.Metadata["BaseDefinition"]!).ShouldBe([baseType]);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Every_daml_classification_has_a_user_visible_fonts_and_colors_entry()
    {
        var formats = (await MefParts.ExportsAsync(typeof(EditorFormatDefinition))).ToList();

        var names = ClassificationBases.Select(row => (string)row[0]).ToList();
        formats.Count.ShouldBe(names.Count);
        foreach (var name in names)
        {
            var format = formats.Single(e => ((IEnumerable<string>)e.Metadata["ClassificationTypeNames"]!).Contains(name));
            format.Metadata["UserVisible"].ShouldBe(true, name);
        }
    }

    [Fact, Trait("Category", "Editor")]
    public void Fonts_and_colors_entries_are_named_after_daml()
    {
        var formatTypes = typeof(DamlClassificationTaggerProvider).Assembly.GetTypes()
            .Where(t => typeof(ClassificationFormatDefinition).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        formatTypes.Count.ShouldBe(ClassificationBases.Count);
        foreach (var type in formatTypes)
        {
            var format = (ClassificationFormatDefinition)Activator.CreateInstance(type, nonPublic: true)!;
            format.DisplayName.ShouldStartWith("Daml - ");
        }
    }

    private static IEnumerable<string?> TypeNames(object? metadata) => metadata switch
    {
        IEnumerable<Type> types => types.Select(t => t.FullName),
        IEnumerable<TypeRef> refs => refs.Select(r => r.FullName),
        Type type => [type.FullName],
        TypeRef typeRef => [typeRef.FullName],
        _ => throw new InvalidOperationException($"Unexpected TagTypes metadata: {metadata?.GetType()}"),
    };


}
