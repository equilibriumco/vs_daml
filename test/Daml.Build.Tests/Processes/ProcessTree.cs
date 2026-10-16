using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Daml.Build.Tests.Processes;

// Lists the processes a test process started, to check they are gone after a kill. Walks
// parent IDs from a Toolhelp snapshot, since Process has no notion of children.
internal static class ProcessTree
{
    public static List<Process> Descendants(int parentId) =>
        Descendants(parentId, Process.GetProcessById(parentId).StartTime);

    // Parent IDs are reused, so a "child" that started before its parent is somebody else's.
    private static List<Process> Descendants(int parentId, DateTime parentStartTime)
    {
        var childrenOf = new Dictionary<int, List<int>>();
        foreach (var (id, parent) in Snapshot())
        {
            if (!childrenOf.TryGetValue(parent, out var children))
            {
                childrenOf[parent] = children = [];
            }
            children.Add(id);
        }

        var found = new List<Process>();
        var pending = new Queue<(int Id, DateTime StartTime)>();
        pending.Enqueue((parentId, parentStartTime));
        while (pending.Count > 0)
        {
            var (id, startTime) = pending.Dequeue();
            if (!childrenOf.TryGetValue(id, out var children))
            {
                continue;
            }
            foreach (var childId in children)
            {
                if (childId != id && Open(childId) is { } child)
                {
                    if (StartTime(child) is { } childStart && childStart >= startTime)
                    {
                        found.Add(child);
                        pending.Enqueue((childId, childStart));
                    }
                    else
                    {
                        child.Dispose();
                    }
                }
            }
        }
        return found;
    }

    private static Process? Open(int id)
    {
        try
        {
            return Process.GetProcessById(id);
        }
        catch (ArgumentException)
        {
            return null; // already gone
        }
    }

    private static DateTime? StartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null; // not ours to look at, or gone
        }
    }

    private static List<(int Id, int ParentId)> Snapshot()
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.SnapProcess, 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var entries = new List<(int Id, int ParentId)>();
        var entry = new NativeMethods.ProcessEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>() };
        for (var more = NativeMethods.Process32First(snapshot, ref entry); more; more = NativeMethods.Process32Next(snapshot, ref entry))
        {
            entries.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
        }
        return entries;
    }

    private static class NativeMethods
    {
        public const uint SnapProcess = 0x2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern SnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32First(SnapshotHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32Next(SnapshotHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }

    private sealed class SnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SnapshotHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }
}
