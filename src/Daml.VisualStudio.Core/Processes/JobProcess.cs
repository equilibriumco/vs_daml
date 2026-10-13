using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using static Daml.VisualStudio.Core.Processes.JobNativeMethods;

namespace Daml.VisualStudio.Core.Processes;

// A process created inside its own kill-on-close Job Object. The process is placed in the job by
// CreateProcess itself, so there's no window in which it, or anything it starts, runs outside;
// assigning a process to a job after Process.Start lets a fast child (dpm.cmd -> dpm.exe) escape.
// Closing the job handle - on Dispose, or when this process dies - ends the whole tree.
//
// Its standard streams are named pipes opened for overlapped I/O: an anonymous pipe can't be,
// and reading one asynchronously on .NET Framework parks a thread-pool thread in ReadFile for
// as long as the pipe is open, which for a language server is forever.
internal sealed class JobProcess : IRunningProcess
{
    // Serializes this runner's creation windows. The explicit handle list restricts our child;
    // unrelated process launchers in the host do not take this lock.
    private static readonly object CreateLock = new();

    private const int PipeBufferSize = 64 * 1024;

    private readonly SafeJobHandle _job;
    private readonly SafeProcessHandle _process;
    private readonly NamedPipeServerStream _stdin;
    private readonly NamedPipeServerStream _stdout;
    private readonly NamedPipeServerStream _stderr;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEvent _exitSignal;
    private readonly RegisteredWaitHandle _exitWait;

    private JobProcess(int id, SafeJobHandle job, SafeProcessHandle process, NamedPipeServerStream stdin, NamedPipeServerStream stdout, NamedPipeServerStream stderr)
    {
        Id = id;
        _job = job;
        _process = process;
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;

        // The event borrows the process handle; _process keeps owning it.
        _exitSignal = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false) };
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) => OnExited(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int Id { get; }

    public Stream StandardInput => _stdin;

    public Stream StandardOutput => _stdout;

    public Stream StandardError => _stderr;

    public Task<int> Exited => _exited.Task;

    public static JobProcess Start(ProcessSpec spec, Action<ProcessRunner.StartStage, int>? hook)
    {
        // CreateProcess gets the program as its application name, which isn't searched on PATH.
        if (!Path.IsPathRooted(spec.FileName))
        {
            throw new ArgumentException($"The program must be given by its full path, not '{spec.FileName}'.", nameof(spec));
        }
        if (!File.Exists(spec.FileName))
        {
            throw new FileNotFoundException($"{spec.FileName} doesn't exist.", spec.FileName);
        }

        string application;
        string arguments;
        if (CommandLine.IsBatchFile(spec.FileName))
        {
            application = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            arguments = CommandLine.ForBatch(spec.FileName, spec.Arguments);
        }
        else
        {
            application = spec.FileName;
            arguments = CommandLine.ForExecutable(spec.Arguments);
        }
        var commandLine = ("\"" + application + "\" " + arguments + "\0").ToCharArray();

        var job = CreateKillOnCloseJob();
        NamedPipeServerStream? stdin = null, stdout = null, stderr = null;
        NamedPipeClientStream? childIn = null, childOut = null, childErr = null;
        SafeProcessHandle? process = null;
        var environment = IntPtr.Zero;
        try
        {
            hook?.Invoke(ProcessRunner.StartStage.JobCreated, 0);

            (stdin, childIn) = CreatePipe(PipeDirection.Out);
            (stdout, childOut) = CreatePipe(PipeDirection.In);
            (stderr, childErr) = CreatePipe(PipeDirection.In);
            environment = Marshal.StringToHGlobalUni(EnvironmentBlock(spec.Environment));

            ProcessInformation info;
            lock (CreateLock)
            {
                info = Create(application, commandLine, environment, spec, job, [childIn.SafePipeHandle, childOut.SafePipeHandle, childErr.SafePipeHandle]);
            }
            CloseHandle(info.Thread);
            process = new SafeProcessHandle(info.Process, ownsHandle: true);

            // The child has its own handles now; holding ours would keep its output pipes open forever.
            childIn.Dispose();
            childOut.Dispose();
            childErr.Dispose();

            hook?.Invoke(ProcessRunner.StartStage.Created, info.ProcessId);
            return new JobProcess(info.ProcessId, job, process, stdin, stdout, stderr);
        }
        catch
        {
            // Whatever got created dies with the job.
            TerminateJobObject(job, 1);
            job.Dispose();
            process?.Dispose();
            childIn?.Dispose();
            childOut?.Dispose();
            childErr?.Dispose();
            stdin?.Dispose();
            stdout?.Dispose();
            stderr?.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
    }

    public void KillTree() => TerminateJobObject(_job, 1);

    public void Dispose()
    {
        _exitWait.Unregister(null);
        _exitSignal.Dispose();
        _job.Dispose();
        _process.Dispose();
        _stdin.Dispose();
        _stdout.Dispose();
        _stderr.Dispose();
        // Nobody will learn the exit code now; a late awaiter must not hang on it.
        _exited.TrySetCanceled();
    }

    private void OnExited()
    {
        // Disposed meanwhile: nobody can be waiting for a code any more.
        if (_process.IsClosed)
        {
            return;
        }
        try
        {
            _exited.TrySetResult(GetExitCodeProcess(_process, out var code) ? code : -1);
        }
        catch (ObjectDisposedException)
        {
            // Lost the race with Dispose.
        }
    }

    // Creates the process with inheritable copies of the child's three pipe ends, which only
    // live for the duration of the call. Callers hold CreateLock.
    private static ProcessInformation Create(string application, char[] commandLine, IntPtr environment, ProcessSpec spec, SafeJobHandle job, SafePipeHandle[] childEnds)
    {
        var inheritable = new IntPtr[childEnds.Length];
        var attributeList = IntPtr.Zero;
        var handleList = IntPtr.Zero;
        var jobList = IntPtr.Zero;
        try
        {
            for (var i = 0; i < childEnds.Length; i++)
            {
                inheritable[i] = InheritableCopy(childEnds[i]);
            }

            // Only the three pipe ends are inherited, not every inheritable handle in the host.
            handleList = Marshal.AllocHGlobal(IntPtr.Size * inheritable.Length);
            Marshal.Copy(inheritable, 0, handleList, inheritable.Length);
            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            attributeList = CreateAttributeList(
                (ProcThreadAttributeHandleList, handleList, IntPtr.Size * inheritable.Length),
                (ProcThreadAttributeJobList, jobList, IntPtr.Size));

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StdInput = inheritable[0],
                    StdOutput = inheritable[1],
                    StdError = inheritable[2],
                },
                AttributeList = attributeList,
            };
            if (!CreateProcess(application, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles: true,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow, environment,
                    string.IsNullOrEmpty(spec.WorkingDirectory) ? null : spec.WorkingDirectory, ref startupInfo, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    string.IsNullOrEmpty(spec.WorkingDirectory) ? $"Couldn't start {spec.FileName}" : $"Couldn't start {spec.FileName} in {spec.WorkingDirectory}");
            }
            return info;
        }
        finally
        {
            foreach (var handle in inheritable)
            {
                if (handle != IntPtr.Zero)
                {
                    CloseHandle(handle);
                }
            }
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            Marshal.FreeHGlobal(handleList);
            Marshal.FreeHGlobal(jobList);
        }
    }

    // Our end supports overlapped I/O; the child's end is plain. A random pipe name and one
    // instance reduce collisions, and both ends are connected before creating the child.
    private static (NamedPipeServerStream Ours, NamedPipeClientStream Child) CreatePipe(PipeDirection ourDirection)
    {
        var name = "daml-vs-" + Guid.NewGuid().ToString("N");
        // With no buffer of its own a write waits for a reader; a child that prints a little and
        // exits before anyone reads must not.
        var ours = new NamedPipeServerStream(name, ourDirection, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, PipeBufferSize, PipeBufferSize);
        NamedPipeClientStream? child = null;
        try
        {
            child = new NamedPipeClientStream(".", name, ourDirection == PipeDirection.In ? PipeDirection.Out : PipeDirection.In,
                PipeOptions.None, TokenImpersonationLevel.None, HandleInheritability.None);
            child.Connect(timeout: 5000); // the instance is there, waiting; this doesn't actually wait
            ours.WaitForConnection();
            return (ours, child);
        }
        catch
        {
            child?.Dispose();
            ours.Dispose();
            throw;
        }
    }

    private static IntPtr InheritableCopy(SafePipeHandle handle)
    {
        var self = GetCurrentProcess();
        if (!DuplicateHandle(self, handle.DangerousGetHandle(), self, out var copy, 0, inheritHandle: true, DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't make a pipe handle for the child");
        }
        return copy;
    }

    private static SafeJobHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't create a job object");
        }
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Couldn't make the job kill its processes on close");
        }
        return job;
    }

    private static IntPtr CreateAttributeList(params (IntPtr Attribute, IntPtr Value, int Size)[] attributes)
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, attributes.Length, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(list, attributes.Length, 0, ref size))
        {
            var error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(error, "Couldn't create a process attribute list");
        }
        foreach (var (attribute, value, valueSize) in attributes)
        {
            if (!UpdateProcThreadAttribute(list, 0, attribute, value, (IntPtr)valueSize, IntPtr.Zero, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
                throw new Win32Exception(error, "Couldn't set a process attribute");
            }
        }
        return list;
    }

    // The inherited environment with the spec's changes, as a sorted, double-NUL-terminated block.
    private static string EnvironmentBlock(IReadOnlyDictionary<string, string?> changes)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = (string?)entry.Value ?? "";
        }
        foreach (var change in changes)
        {
            if (change.Value is null)
            {
                variables.Remove(change.Key);
            }
            else
            {
                variables[change.Key] = change.Value;
            }
        }

        var block = new StringBuilder();
        foreach (var variable in variables.Where(v => v.Key.Length > 0))
        {
            block.Append(variable.Key).Append('=').Append(variable.Value).Append('\0');
        }
        // StringToHGlobalUni adds the final terminator; an empty block still needs two.
        return block.Append('\0').ToString();
    }
}
