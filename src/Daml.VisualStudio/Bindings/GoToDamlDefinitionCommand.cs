using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Commands;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Editor.Commanding;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Bindings;

/// <summary>Go to Daml Definition, in a C# editor.</summary>
internal sealed class GoToDamlDefinitionCommandArgs : EditorCommandArgs
{
    public GoToDamlDefinitionCommandArgs(ITextView textView, ITextBuffer subjectBuffer)
        : base(textView, subjectBuffer)
    {
    }
}

/// <summary>Routes the command from the code window's context menu to the editor's handlers, with no package to load.</summary>
internal static class GoToDamlDefinitionCommandBinding
{
    [Export]
    [CommandBinding(DamlMenuIds.CommandSetString, (uint)DamlMenuIds.GoToDamlDefinition, typeof(GoToDamlDefinitionCommandArgs))]
    internal static readonly CommandBindingDefinition? Definition = null;
}

/// <summary>
/// Go to Daml Definition in a C# editor: offered while the name at the caret stands for a Daml
/// declaration, and going there, or explaining why not.
/// </summary>
[Export(typeof(ICommandHandler))]
[ContentType("CSharp")]
[Name(nameof(GoToDamlDefinitionCommandHandler))]
internal sealed class GoToDamlDefinitionCommandHandler : ICommandHandler<GoToDamlDefinitionCommandArgs>
{
    private readonly DamlBindingService _service;
    private readonly DamlNavigator _navigator;
    private readonly JoinableTaskFactory _joinableTaskFactory;

    [ImportingConstructor]
    public GoToDamlDefinitionCommandHandler(DamlBindingService service, DamlNavigator navigator, JoinableTaskContext joinableTaskContext)
    {
        _service = service;
        _navigator = navigator;
        _joinableTaskFactory = joinableTaskContext.Factory;
    }

    public string DisplayName => "Go to Daml Definition";

    public CommandState GetCommandState(GoToDamlDefinitionCommandArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // the shell asks for command state on the UI thread
        return DamlCaretBindings.Of(args.TextView)?.AtCaret == true ? CommandState.Available : CommandState.Unavailable;
    }

    public bool ExecuteCommand(GoToDamlDefinitionCommandArgs args, CommandExecutionContext executionContext)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var caret = args.TextView.Caret.Position.BufferPosition;
        _joinableTaskFactory.RunAsync(async () =>
        {
            await TaskScheduler.Default;
            if (caret.Snapshot.GetOpenDocumentInCurrentContextWithChanges() is { } document
                && await _service.FindAsync(document, caret.Position, CancellationToken.None) is { } found)
            {
                await _navigator.GoAsync(found.Found, found.Index, CancellationToken.None);
            }
        }).FileAndForget("daml/bindings/goToDefinition");
        return true;
    }
}
