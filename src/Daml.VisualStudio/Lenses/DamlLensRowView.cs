using System;
using System.ComponentModel.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Daml.VisualStudio.LanguageServer.Lenses;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Lenses;

/// <summary>Leaves a line's worth of room above every line that has a lens row.</summary>
[Export(typeof(ILineTransformSourceProvider))]
[ContentType(DamlContentDefinition.ContentTypeName)]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class DamlLensSpaceProvider : ILineTransformSourceProvider
{
    private readonly DamlLensSources _sources;

    [ImportingConstructor]
    public DamlLensSpaceProvider(DamlLensSources sources) => _sources = sources;

    public ILineTransformSource Create(IWpfTextView textView) => new Space(textView, DamlLensRows.For(textView.TextBuffer, _sources));

    private sealed class Space(IWpfTextView view, DamlLensRows rows) : ILineTransformSource
    {
        public LineTransform GetLineTransform(ITextViewLine line, double yPosition, ViewRelativePosition placement) =>
            line.IsFirstTextViewLineForSnapshotLine && rows.At(line.Start.GetContainingLine()) is not null
                ? new LineTransform(DamlLensRowView.Height(view), 0, 1.0)
                : line.DefaultLineTransform;
    }
}

/// <summary>Draws the lens rows of a Daml editor.</summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType(DamlContentDefinition.ContentTypeName)]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class DamlLensRowViewFactory : IWpfTextViewCreationListener
{
    public const string LayerName = "DamlLensRows";

#pragma warning disable CS0649, CS0169
    [Export(typeof(AdornmentLayerDefinition))]
    [Name(LayerName)]
    [Order(After = PredefinedAdornmentLayers.Text)]
    [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "MEF reads the field's attributes; it is never assigned.")]
    private static AdornmentLayerDefinition? s_layer;
#pragma warning restore CS0649, CS0169

    private readonly DamlLensSources _sources;
    private readonly IEditorFormatMapService _formats;
    private readonly JoinableTaskContext _joinableTaskContext;

    [ImportingConstructor]
    public DamlLensRowViewFactory(DamlLensSources sources, IEditorFormatMapService formats, JoinableTaskContext joinableTaskContext)
    {
        _sources = sources;
        _formats = formats;
        _joinableTaskContext = joinableTaskContext;
    }

    public void TextViewCreated(IWpfTextView textView) =>
        _ = new DamlLensRowView(textView, DamlLensRows.For(textView.TextBuffer, _sources), _sources, _formats.GetEditorFormatMap(textView), _joinableTaskContext.Factory);
}

/// <summary>
/// The rows of one view: "▶ Script results" and later segments, above their declarations, in
/// the colour of comments (so they follow the theme and Fonts and Colors), lined up with the
/// declaration's first character. Only the lines being laid out are drawn.
/// </summary>
internal sealed class DamlLensRowView
{
    private readonly IWpfTextView _view;
    private readonly DamlLensRows _rows;
    private readonly DamlLensSources _sources;
    private readonly IEditorFormatMap _formats;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IAdornmentLayer _layer;
    private Brush _foreground = Brushes.Gray;
    private int _relayoutPending;

    public DamlLensRowView(IWpfTextView view, DamlLensRows rows, DamlLensSources sources, IEditorFormatMap formats, JoinableTaskFactory joinableTaskFactory)
    {
        _view = view;
        _rows = rows;
        _sources = sources;
        _formats = formats;
        _joinableTaskFactory = joinableTaskFactory;
        _layer = view.GetAdornmentLayer(DamlLensRowViewFactory.LayerName);
        UpdateColours();
        _view.LayoutChanged += OnLayoutChanged;
        _view.Closed += OnClosed;
        _formats.FormatMappingChanged += OnFormatMappingChanged;
        _rows.Changed += OnRowsChanged;
        _rows.AddUser();
    }

    /// <summary>The room a row takes: a little less than a line of text.</summary>
    public static double Height(ITextView view) => Math.Ceiling(view.LineHeight * 0.9);

    private void OnClosed(object sender, EventArgs e)
    {
        _view.LayoutChanged -= OnLayoutChanged;
        _view.Closed -= OnClosed;
        _formats.FormatMappingChanged -= OnFormatMappingChanged;
        _rows.Changed -= OnRowsChanged;
        _rows.RemoveUser();
    }

    // The format map raises this on the UI thread, where the editor lives.
    private void OnFormatMappingChanged(object sender, FormatItemsEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        UpdateColours();
        Relayout();
    }

    // Rows change on a pool thread; one relayout on the UI thread covers any burst of them.
    private void OnRowsChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _relayoutPending, 1) != 0)
        {
            return;
        }
        _joinableTaskFactory.RunAsync(async () =>
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            Volatile.Write(ref _relayoutPending, 0);
            if (!_view.IsClosed)
            {
                Relayout();
            }
        }).FileAndForget("daml/lenses/relayout");
    }

    private void UpdateColours()
    {
        var properties = _formats.GetProperties(PredefinedClassificationTypeNames.Comment);
        _foreground = properties[EditorFormatDefinition.ForegroundBrushId] as Brush ?? Brushes.Gray;
    }

    // Line transforms are only asked for during layout, so changed rows need a layout pass.
    private void Relayout()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_view.InLayout || _view.TextViewLines?.FirstVisibleLine is not { } first)
        {
            return;
        }
        _view.DisplayTextLineContainingBufferPosition(first.Start, first.Top - _view.ViewportTop, ViewRelativePosition.Top);
    }

    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        var height = Height(_view);
        foreach (var line in e.NewOrReformattedLines)
        {
            if (!line.IsFirstTextViewLineForSnapshotLine)
            {
                continue;
            }
            var snapshotLine = line.Start.GetContainingLine();
            if (_rows.At(snapshotLine) is not { } segments)
            {
                continue;
            }
            var row = new TextBlock
            {
                Foreground = _foreground,
                FontFamily = _view.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily,
                FontSize = _view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize * 0.85,
                Height = height,
            };
            for (var i = 0; i < segments.Count; i++)
            {
                if (i > 0)
                {
                    row.Inlines.Add(new Run(" | "));
                }
                row.Inlines.Add(Link(segments[i]));
            }
            Canvas.SetLeft(row, IndentLeft(line, snapshotLine));
            Canvas.SetTop(row, line.TextTop - height);
            _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, line.Extent, segments, row, null);
        }
    }

    private Hyperlink Link(LensSegment segment)
    {
        var link = new Hyperlink(new Run(segment.Text))
        {
            Foreground = _foreground,
            TextDecorations = null,
            ToolTip = segment.AutomationName,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        AutomationProperties.SetName(link, segment.AutomationName);
        link.Click += (_, _) => _joinableTaskFactory.RunAsync(() => _sources.InvokeAsync(segment, CancellationToken.None)).FileAndForget("daml/lenses/click");
        return link;
    }

    // Lined up with the declaration's first character, not the start of the line.
    private static double IndentLeft(ITextViewLine line, ITextSnapshotLine snapshotLine)
    {
        var text = snapshotLine.GetText();
        var indent = 0;
        while (indent < text.Length && char.IsWhiteSpace(text[indent]))
        {
            indent++;
        }
        return indent < text.Length ? line.GetCharacterBounds(snapshotLine.Start + indent).Left : line.TextLeft;
    }
}
