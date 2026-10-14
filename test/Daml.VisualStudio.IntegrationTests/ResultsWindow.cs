using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;

namespace Daml.VisualStudio.IntegrationTests;

// A script results window as a user meets it: found by its caption, its page read and clicked
// through the browser control inside it.
internal static class ResultsWindow
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    public static IVsWindowFrame? Find(string caption)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var frame in Frames())
        {
            if (Caption(frame) == caption)
            {
                return frame;
            }
        }
        return null;
    }

    public static int CountCaptionedLike(Func<string, bool> caption)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var count = 0;
        foreach (var frame in Frames())
        {
            if (Caption(frame) is { } text && caption(text))
            {
                count++;
            }
        }
        return count;
    }

    private static string? Caption(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out var title)) ? title as string : null;
    }

    public static async Task<IVsWindowFrame?> WaitForAsync(string caption)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (Find(caption) is { } frame)
            {
                return frame;
            }
            await Task.Delay(250);
        }
        return null;
    }

    public static void Close(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ErrorHandler.ThrowOnFailure(frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave));
    }

    /// <summary>
    /// Closes every results window. The tests share one Visual Studio, and windows stay open when
    /// a test closes its folder; every test's script is "setup", so a window left over from an
    /// earlier test would be found by its caption instead of the test's own.
    /// </summary>
    public static void CloseAll()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var results = new List<IVsWindowFrame>();
        foreach (var frame in Frames())
        {
            if (Caption(frame) is { } text && text.StartsWith("Script: ", StringComparison.Ordinal))
            {
                results.Add(frame);
            }
        }
        foreach (var frame in results)
        {
            Close(frame);
        }
    }

    /// <summary>The browser of a results window once it is up; null if it never comes.</summary>
    public static async Task<WebView2?> BrowserOfAsync(IVsWindowFrame frame)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var pane))
                && pane is WindowPane { Content: DependencyObject content }
                && Descendants(content).OfType<WebView2>().FirstOrDefault() is { CoreWebView2: not null } browser)
            {
                return browser;
            }
            await Task.Delay(250);
        }
        return null;
    }

    /// <summary>Runs <paramref name="script"/> in the page and returns its JSON result as a token.</summary>
    public static async Task<JToken?> EvalAsync(WebView2 browser, string script)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var json = await browser.ExecuteScriptAsync(script);
        return json is null ? null : JToken.Parse(json);
    }

    /// <summary>Waits until the page's text satisfies <paramref name="accept"/>; returns the last text seen.</summary>
    public static async Task<string> WaitForTextAsync(WebView2 browser, Func<string, bool> accept)
    {
        var deadline = DateTime.UtcNow + Patience;
        var text = "";
        while (DateTime.UtcNow < deadline)
        {
            text = (string?)await EvalAsync(browser, "document.body ? document.body.innerText : ''") ?? "";
            if (accept(text))
            {
                return text;
            }
            await Task.Delay(500);
        }
        return text;
    }

    /// <summary>
    /// A real mouse click in the middle of the first element matching <paramref name="selector"/>,
    /// as the user would make it, so the browser treats it as the user's.
    /// </summary>
    public static async Task<bool> ClickAsync(WebView2 browser, string selector)
    {
        var rect = await EvalAsync(browser, $$"""
            (() => { const e = document.querySelector({{JsonString(selector)}}); if (!e) return null;
                     e.scrollIntoView({block: 'center'}); const r = e.getBoundingClientRect();
                     return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()
            """);
        if (rect is not JObject { } point)
        {
            return false;
        }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var screen = browser.PointToScreen(new Point((double)point["x"]!, (double)point["y"]!));
        SetCursorPos((int)screen.X, (int)screen.Y);
        mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
        return true;
    }

    /// <summary>The browser processes of the extension's own WebView2 profile.</summary>
    public static int BrowserProcesses()
    {
        using var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'");
        return search.Get().Cast<ManagementObject>()
            .Count(p => p["CommandLine"] is string line && line.IndexOf(@"DamlVisualStudio\WebView2", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static long DevenvPrivateBytes()
    {
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        return self.PrivateMemorySize64;
    }

    public static string JsonString(string text) => new JValue(text).ToString(Newtonsoft.Json.Formatting.None);

    private static IEnumerable<IVsWindowFrame> Frames()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var shell = ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell ?? throw new InvalidOperationException("SVsUIShell is not available.");
        ErrorHandler.ThrowOnFailure(shell.GetToolWindowEnum(out var frames));
        var frame = new IVsWindowFrame[1];
        while (frames.Next(1, frame, out var fetched) == VSConstants.S_OK && fetched == 1)
        {
            yield return frame[0];
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            if (node is System.Windows.Controls.ContentControl { Content: DependencyObject content })
            {
                pending.Push(content);
            }
            var count = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
            for (var i = 0; i < count; i++)
            {
                pending.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
