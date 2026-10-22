using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Microsoft.Internal.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;

namespace Equilibrium.Daml.VisualStudio.Bindings;

/// <summary>
/// "EQ Daml: C# References": the C# uses of one Daml declaration, in Visual Studio's own table control
/// (project, file, line, code), double-click to go there. Each lens click fills it again.
/// </summary>
[Guid(PackageGuids.CSharpReferencesWindowString)]
public sealed class CSharpReferencesToolWindow : ToolWindowPane
{
    /// <summary>The table manager this window's table, and its navigation, belong to.</summary>
    public const string ManagerIdentifier = "EqDamlCSharpReferences";

    // Visual Studio frames the pane's content when it creates the window; the table comes later.
    private readonly ContentControl _host = new();
    private CSharpReferencesTable? _table;
    private IWpfTableControl? _control;

    /// <summary>Created by Visual Studio through the package.</summary>
    public CSharpReferencesToolWindow()
        : base(null)
    {
        Caption = "EQ Daml: C# References";
        Content = _host;
    }

    /// <summary>Shows <paramref name="references"/> as the uses of <paramref name="declaration"/>.</summary>
    internal void Show(string declaration, IReadOnlyList<CSharpReference> references)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_table is null && ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) is IComponentModel components)
        {
            var manager = components.GetService<ITableManagerProvider>().GetTableManager(ManagerIdentifier);
            _table = new CSharpReferencesTable();
            manager.AddSource(_table, StandardTableKeyNames.ProjectName, StandardTableKeyNames.DocumentName, StandardTableKeyNames.Line, StandardTableKeyNames.Text);
            _control = components.GetService<IWpfTableControlProvider>().CreateControl(
                manager,
                true,
                [
                    new ColumnState2(StandardTableColumnDefinitions.ProjectName, isVisible: true, width: 160),
                    new ColumnState2(StandardTableColumnDefinitions.DocumentName, isVisible: true, width: 220),
                    new ColumnState2(StandardTableColumnDefinitions.Line, isVisible: true, width: 60),
                    new ColumnState2(StandardTableColumnDefinitions.Text, isVisible: true, width: 600),
                ],
                StandardTableColumnDefinitions.ProjectName,
                StandardTableColumnDefinitions.DocumentName,
                StandardTableColumnDefinitions.Line,
                StandardTableColumnDefinitions.Text);
            _host.Content = _control.Control;
        }
        Caption = $"EQ Daml: C# References — {declaration} ({references.Count})";
        _table?.Replace(references);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _control?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The rows of the references window, one per C# use.</summary>
internal sealed class CSharpReferencesTable : ITableDataSource
{
    private readonly List<ITableDataSink> _sinks = [];
    private IReadOnlyList<ITableEntry> _entries = [];

    public string SourceTypeIdentifier => StandardTableDataSources.ErrorTableDataSource;

    public string Identifier => CSharpReferencesToolWindow.ManagerIdentifier;

    public string DisplayName => "Daml C# references";

    public IDisposable Subscribe(ITableDataSink sink)
    {
        lock (_sinks)
        {
            _sinks.Add(sink);
        }
        sink.AddEntries(_entries, removeAllEntries: true);
        return new Subscription(this, sink);
    }

    public void Replace(IReadOnlyList<CSharpReference> references)
    {
        _entries = references.Select(r => (ITableEntry)new CSharpReferenceEntry(r)).ToList();
        List<ITableDataSink> sinks;
        lock (_sinks)
        {
            sinks = [.. _sinks];
        }
        foreach (var sink in sinks)
        {
            sink.AddEntries(_entries, removeAllEntries: true);
        }
    }

    private sealed class Subscription(CSharpReferencesTable table, ITableDataSink sink) : IDisposable
    {
        public void Dispose()
        {
            lock (table._sinks)
            {
                table._sinks.Remove(sink);
            }
        }
    }
}

/// <summary>One C# use in the references window; lines and columns go to the table from zero.</summary>
internal sealed class CSharpReferenceEntry(CSharpReference reference) : ITableEntry
{
    public object Identity => reference;

    public bool TryGetValue(string keyName, out object content)
    {
        content = keyName switch
        {
            StandardTableKeyNames.ProjectName => reference.Project,
            StandardTableKeyNames.DocumentName => reference.FilePath,
            StandardTableKeyNames.Line => reference.Line - 1,
            StandardTableKeyNames.Column => reference.Column - 1,
            StandardTableKeyNames.Text => reference.Code,
            _ => null!,
        };
        return content is not null;
    }

    public bool CanSetValue(string keyName) => false;

    public bool TrySetValue(string keyName, object content) => false;
}
