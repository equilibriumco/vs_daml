using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;

namespace Equilibrium.Daml.VisualStudio.Tests;

// Minimal in-memory Output window: remembers panes and every string written to them.
internal sealed class FakeOutputWindow : IVsOutputWindow
{
    private readonly List<FakePane> _panes = new();

    public IReadOnlyList<FakePane> Panes
    {
        get
        {
            lock (_panes)
            {
                return _panes.ToList();
            }
        }
    }

    public int CreatePane(ref Guid rguidPane, string pszPaneName, int fInitVisible, int fClearWithSolution)
    {
        var id = rguidPane;
        lock (_panes)
        {
            if (!_panes.Any(p => p.Id == id))
            {
                _panes.Add(new FakePane(id, pszPaneName));
            }
        }
        return VSConstants.S_OK;
    }

    public int GetPane(ref Guid rguidPane, out IVsOutputWindowPane? ppPane)
    {
        var id = rguidPane;
        lock (_panes)
        {
            ppPane = _panes.FirstOrDefault(p => p.Id == id);
        }
        return ppPane is null ? VSConstants.E_FAIL : VSConstants.S_OK;
    }

    public int DeletePane(ref Guid rguidPane) => VSConstants.E_NOTIMPL;

    // Same as CreatePane + GetPane, for tests that stand in for the VS service calls.
    public FakePane Create(Guid id, string title)
    {
        lock (_panes)
        {
            var pane = _panes.FirstOrDefault(p => p.Id == id);
            if (pane is null)
            {
                pane = new FakePane(id, title);
                _panes.Add(pane);
            }
            return pane;
        }
    }

    internal sealed class FakePane : IVsOutputWindowPane
    {
        private readonly List<string> _lines = new();

        public FakePane(Guid id, string title)
        {
            Id = id;
            Title = title;
        }

        public Guid Id { get; }

        public string Title { get; }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToList();
                }
            }
        }

        /// <summary>How many of the next writes fail the way a pane that is gone does.</summary>
        public int FailingWrites { get; set; }

        public int OutputString(string pszOutputString) => Append(pszOutputString);

        public int OutputStringThreadSafe(string pszOutputString) => Append(pszOutputString);

        private int Append(string text)
        {
            lock (_lines)
            {
                if (FailingWrites > 0)
                {
                    FailingWrites--;
                    throw new InvalidOperationException("The pane is gone.");
                }
                _lines.Add(text);
            }
            return VSConstants.S_OK;
        }

        public int Activate() => VSConstants.S_OK;

        public int Hide() => VSConstants.S_OK;

        public int Clear() => VSConstants.S_OK;

        public int FlushToTaskList() => VSConstants.S_OK;

        public int OutputTaskItemString(string pszOutputString, VSTASKPRIORITY nPriority, VSTASKCATEGORY nCategory, string pszSubcategory, int nBitmap, string pszFilename, uint nLineNum, string pszTaskItemText) => VSConstants.E_NOTIMPL;

        public int OutputTaskItemStringEx(string pszOutputString, VSTASKPRIORITY nPriority, VSTASKCATEGORY nCategory, string pszSubcategory, int nBitmap, string pszFilename, uint nLineNum, string pszTaskItemText, string pszLookupKwd) => VSConstants.E_NOTIMPL;

        public int GetName(ref string pbstrPaneName)
        {
            pbstrPaneName = Title;
            return VSConstants.S_OK;
        }

        public int SetName(string pszPaneName) => VSConstants.E_NOTIMPL;
    }
}
