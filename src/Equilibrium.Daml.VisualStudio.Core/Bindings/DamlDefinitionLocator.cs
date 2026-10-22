using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Equilibrium.Daml.VisualStudio.Core.Syntax;

namespace Equilibrium.Daml.VisualStudio.Core.Bindings;

/// <summary>Where a Daml declaration is: the name that declares it. Lines and columns count from zero.</summary>
/// <param name="FilePath">The <c>.daml</c> file.</param>
/// <param name="Line">The line of the name.</param>
/// <param name="Column">Where the name starts in it.</param>
public sealed record DamlDefinition(string FilePath, int Line, int Column);

/// <summary>What looking for a declaration found: the place, or why there is none.</summary>
/// <param name="Definition">The place; null when it wasn't found.</param>
/// <param name="Problem">Why not, when <paramref name="Definition"/> is null.</param>
public sealed record DamlDefinitionLookup(DamlDefinition? Definition, string? Problem);

/// <summary>
/// Finds a Daml symbol's declaration in a package's sources: the module through the package's
/// <c>source</c>, then the declaration in it by its block, as the editor's own layout analysis
/// sees the file. A template, interface or data type is its declaration line; a choice is its
/// declaration inside the template or interface (the implicit <c>Archive</c> is the template); a
/// field is its line in the <c>with</c> block; a constructor is where the data type lists it.
/// </summary>
public sealed class DamlDefinitionLocator
{
    private readonly IDamlFileSystem _files;
    private readonly ModulePathResolver _modules;

    /// <summary>Creates a locator reading through <paramref name="files"/>.</summary>
    public DamlDefinitionLocator(IDamlFileSystem files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _modules = new ModulePathResolver(files);
    }

    /// <summary>The declaration of <paramref name="symbol"/> in the package of <paramref name="manifestPath"/>.</summary>
    public DamlDefinitionLookup Find(DamlSymbolId symbol, string manifestPath)
    {
        if (symbol is null)
        {
            throw new ArgumentNullException(nameof(symbol));
        }
        DamlPackageManifest package;
        string path;
        IReadOnlyList<LineTokens> lines;
        try
        {
            var read = new YamlManifestReader(_files).ReadPackage(manifestPath);
            if (read.Value is not { } manifest)
            {
                return Missing($"{manifestPath} couldn't be read: {string.Join(" ", read.Diagnostics.Select(d => d.Message))}");
            }
            package = manifest;
            var module = _modules.Resolve(symbol.Module, package, [package]);
            if (module.Status != ModuleResolutionStatus.Found)
            {
                return Missing(module.Status switch
                {
                    ModuleResolutionStatus.Ambiguous => $"Several files of {package.Name ?? manifestPath} declare module {symbol.Module}.",
                    ModuleResolutionStatus.Stale => $"The file for module {symbol.Module} in {package.Name ?? manifestPath} declares another module; the sources changed since the last build.",
                    _ => module.Reason ?? $"{package.Name ?? manifestPath} has no module {symbol.Module}.",
                });
            }
            path = module.Path!;
            lines = LineTokens.Lex(_files.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Missing($"The sources of {symbol.Module} couldn't be read: {ex.Message}");
        }
        return new Declarations(lines).Of(symbol) is { } at
            ? new DamlDefinitionLookup(new DamlDefinition(path, at.Line, at.Column), null)
            : Missing($"{Path.GetFileName(path)} doesn't declare {Describe(symbol)}; the sources may have changed since the last build.");
    }

    private static DamlDefinitionLookup Missing(string problem) => new(null, problem);

    private static string Describe(DamlSymbolId symbol) =>
        symbol.Member is null ? $"{symbol.Kind.ToString().ToLowerInvariant()} {symbol.Entity}" : $"{symbol.Kind.ToString().ToLowerInvariant()} {symbol.Entity}.{symbol.Member}";

    // The declarations of one file, found by its layout and tokens.
    private sealed class Declarations
    {
        private readonly IReadOnlyList<LineTokens> _lines;
        private readonly DamlBlock _module;

        public Declarations(IReadOnlyList<LineTokens> lines)
        {
            _lines = lines;
            _module = LayoutAnalyzer.Analyze(lines);
        }

        public (int Line, int Column)? Of(DamlSymbolId symbol)
        {
            var owner = _module.Children.FirstOrDefault(b => b.Name == symbol.Entity && b.Kind is DamlBlockKind.Template or DamlBlockKind.Interface or DamlBlockKind.Data);
            if (owner is null)
            {
                return null;
            }
            var member = symbol.Member ?? "";
            switch (symbol.Kind)
            {
                case DamlSymbolKind.Choice:
                    if (Choice(owner, member) is { } choice)
                    {
                        return Name(choice, member);
                    }
                    return member == "Archive" && owner.Kind == DamlBlockKind.Template ? Name(owner, symbol.Entity) : null;
                case DamlSymbolKind.Constructor:
                    return Constructor(owner, member);
                case DamlSymbolKind.Field:
                    var dot = member.IndexOf('.');
                    if (dot < 0)
                    {
                        return owner.Children.FirstOrDefault(b => b.Kind == DamlBlockKind.WithBlock) is { } fields ? Field(fields.StartLine, 0, fields.EndLine, member) : null;
                    }
                    var (parent, field) = (member.Substring(0, dot), member.Substring(dot + 1));
                    if (Choice(owner, parent)?.Children.FirstOrDefault(b => b.Kind == DamlBlockKind.WithBlock) is { } argument)
                    {
                        return Field(argument.StartLine, 0, argument.EndLine, field);
                    }
                    return Constructor(owner, parent) is { } constructor ? Field(constructor.Line, constructor.Column, owner.EndLine, field) : null;
                default:
                    return Name(owner, symbol.Entity);
            }
        }

        private static DamlBlock? Choice(DamlBlock owner, string name) =>
            Descendants(owner).FirstOrDefault(b => b.Kind == DamlBlockKind.Choice && b.Name == name);

        private static IEnumerable<DamlBlock> Descendants(DamlBlock block) =>
            block.Children.SelectMany(child => new[] { child }.Concat(Descendants(child)));

        // The name in the block's header: `template Asset`, `choice Give : …`, `data Point = …`.
        private (int Line, int Column)? Name(DamlBlock block, string name) =>
            Tokens(block.StartLine, 0, block.HeaderEndLine).Where(t => t.Kind == DamlTokenKind.ConId && t.Text == name).Select(t => ((int, int)?)(t.Line, t.Column)).FirstOrDefault();

        // `= Circle with …`, `| Square Decimal`, `data Colour = Red | Green | Blue`.
        private (int Line, int Column)? Constructor(DamlBlock data, string name)
        {
            Token? previous = null;
            foreach (var token in Tokens(data.StartLine, 0, data.EndLine))
            {
                if (token.Kind == DamlTokenKind.ConId && token.Text == name && previous is { Text: "=" or "|" })
                {
                    return (token.Line, token.Column);
                }
                previous = token;
            }
            return null;
        }

        // `name : Type`, or `left, right : Type` with the name among others.
        private (int Line, int Column)? Field(int fromLine, int fromColumn, int toLine, string name)
        {
            var tokens = Tokens(fromLine, fromColumn, toLine).ToList();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != DamlTokenKind.VarId || tokens[i].Text != name)
                {
                    continue;
                }
                var next = i + 1;
                while (next + 1 < tokens.Count && tokens[next].Text == "," && tokens[next + 1].Kind == DamlTokenKind.VarId)
                {
                    next += 2;
                }
                if (next < tokens.Count && tokens[next].Text == ":")
                {
                    return (tokens[i].Line, tokens[i].Column);
                }
            }
            return null;
        }

        // The tokens that mean something, from a place to the end of a line, in order.
        private IEnumerable<Token> Tokens(int fromLine, int fromColumn, int toLine)
        {
            for (var line = fromLine; line <= toLine && line < _lines.Count; line++)
            {
                foreach (var token in _lines[line].Tokens)
                {
                    if ((line > fromLine || token.Start >= fromColumn)
                        && token.Kind is not (DamlTokenKind.Whitespace or DamlTokenKind.LineComment or DamlTokenKind.DocComment or DamlTokenKind.BlockComment or DamlTokenKind.Pragma))
                    {
                        yield return new Token(line, token.Start, token.Kind, _lines[line].Text.Substring(token.Start, token.Length));
                    }
                }
            }
        }

        private sealed record Token(int Line, int Column, DamlTokenKind Kind, string Text);
    }
}
