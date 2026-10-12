using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Daml.VisualStudio.Classification;
using Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.Composition.Reflection;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

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
    public async Task The_tagger_provider_serves_classification_tags_for_daml_buffers()
    {
        var export = (await ExportsAsync(typeof(ITaggerProvider))).Single();

        ((IEnumerable<string>)export.Metadata["ContentTypes"]!).ShouldBe(["daml"]);
        TypeNames(export.Metadata["TagTypes"]).ShouldBe([typeof(IClassificationTag).FullName]);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Daml_buffers_get_their_own_word_navigator()
    {
        var export = (await ExportsAsync(typeof(Microsoft.VisualStudio.Text.Operations.ITextStructureNavigatorProvider))).Single();

        ((IEnumerable<string>)export.Metadata["ContentTypes"]!).ShouldBe(["daml"]);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Double_quotes_get_native_brace_completion_in_daml_buffers()
    {
        var export = (await ExportsAsync(typeof(Microsoft.VisualStudio.Text.BraceCompletion.IBraceCompletionDefaultProvider))).Single();

        ((IEnumerable<string>)export.Metadata["ContentTypes"]!).ShouldBe(["daml"]);
        ((IEnumerable<char>)export.Metadata["OpeningBraces"]!).ShouldBe(['"']);
        ((IEnumerable<char>)export.Metadata["ClosingBraces"]!).ShouldBe(['"']);
    }

    [Theory, Trait("Category", "Editor")]
    [MemberData(nameof(ClassificationBases))]
    public async Task Each_daml_classification_derives_from_a_predefined_one(string name, string baseType)
    {
        var exports = await ExportsAsync(typeof(ClassificationTypeDefinition));

        var export = exports.Single(e => (string)e.Metadata["Name"]! == name);
        ((IEnumerable<string>)export.Metadata["BaseDefinition"]!).ShouldBe([baseType]);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Every_daml_classification_has_a_user_visible_fonts_and_colors_entry()
    {
        var formats = (await ExportsAsync(typeof(EditorFormatDefinition))).ToList();

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

    private static async Task<IEnumerable<ExportDefinition>> ExportsAsync(Type contract)
    {
        var discovery = PartDiscovery.Combine(
            new AttributedPartDiscoveryV1(Resolver.DefaultInstance),
            new AttributedPartDiscovery(Resolver.DefaultInstance, isNonPublicSupported: true));
        var parts = await discovery.CreatePartsAsync(typeof(DamlPackage).Assembly);
        return parts.Parts
            .SelectMany(p => p.ExportingMembers.SelectMany(m => m.Value).Concat(p.ExportedTypes))
            .Where(e => e.ContractName == contract.FullName);
    }
}
