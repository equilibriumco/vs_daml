using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Processes;

namespace Equilibrium.Daml.TestHelpers;

/// <summary>
/// <see cref="IProcessRunner"/> that answers from a script instead of starting anything. A call
/// nobody scripted throws, so a test can't pass by accident on an unexpected command. A later
/// entry for the same command overrides an earlier one, so a test can change a default answer.
/// <see cref="Start"/> hands out <see cref="FakeRunningProcess"/> instances the test ends by hand.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(string FileName, string[] Arguments, Func<ProcessSpec, Task<ProcessResult>> Answer)> _script = new();
    private readonly List<ProcessSpec> _calls = new();
    private readonly List<FakeRunningProcess> _started = new();
    private Exception? _startFailure;

    /// <summary>Everything run or started so far, oldest first; a copy, safe to read while calls come in.</summary>
    public IReadOnlyList<ProcessSpec> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Answers <paramref name="fileName"/> with exactly <paramref name="arguments"/> by <paramref name="result"/>.</summary>
    public FakeProcessRunner On(string fileName, string[] arguments, ProcessResult result)
    {
        lock (_calls)
        {
            _script.Add((fileName, arguments, _ => Task.FromResult(result)));
        }
        return this;
    }

    /// <summary>Shorthand for a successful run printing <paramref name="stdout"/>.</summary>
    public FakeProcessRunner On(string fileName, string[] arguments, string stdout) =>
        On(fileName, arguments, new ProcessResult(0, stdout, ""));

    /// <summary>Makes the call fail with <paramref name="exception"/>, as a timeout or a missing program would.</summary>
    public FakeProcessRunner On(string fileName, string[] arguments, Exception exception)
    {
        lock (_calls)
        {
            _script.Add((fileName, arguments, _ => Task.FromException<ProcessResult>(exception)));
        }
        return this;
    }

    /// <summary>
    /// Answers with what <paramref name="run"/> does with the call, for a program whose effect
    /// on disk matters: it sees the working directory and environment it was started with.
    /// </summary>
    public FakeProcessRunner On(string fileName, string[] arguments, Func<ProcessSpec, Task<ProcessResult>> run)
    {
        lock (_calls)
        {
            _script.Add((fileName, arguments, run));
        }
        return this;
    }

    /// <inheritdoc />
    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        (string FileName, string[] Arguments, Func<ProcessSpec, Task<ProcessResult>> Answer) entry;
        lock (_calls)
        {
            _calls.Add(spec);
            entry = _script.LastOrDefault(s =>
                string.Equals(s.FileName, spec.FileName, StringComparison.OrdinalIgnoreCase) && s.Arguments.SequenceEqual(spec.Arguments));
        }
        if (entry.Answer is null)
        {
            throw new InvalidOperationException($"Unscripted process: {spec.FileName} {string.Join(" ", spec.Arguments)}");
        }
        return entry.Answer(spec);
    }

    /// <summary>Processes handed out by <see cref="Start"/>, oldest first; a copy.</summary>
    public IReadOnlyList<FakeRunningProcess> Started
    {
        get
        {
            lock (_calls)
            {
                return [.. _started];
            }
        }
    }

    /// <summary>Makes the next <see cref="Start"/> calls throw <paramref name="exception"/> until cleared with <c>null</c>.</summary>
    public void FailStarts(Exception? exception) => _startFailure = exception;

    /// <inheritdoc />
    public IRunningProcess Start(ProcessSpec spec)
    {
        lock (_calls)
        {
            _calls.Add(spec);
            if (_startFailure is not null)
            {
                throw _startFailure;
            }
            var process = new FakeRunningProcess(spec);
            _started.Add(process);
            return process;
        }
    }
}
