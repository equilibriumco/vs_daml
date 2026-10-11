using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Processes;

namespace Daml.TestHelpers;

/// <summary>
/// <see cref="IProcessRunner"/> that answers from a script instead of starting anything. A call
/// nobody scripted throws, so a test can't pass by accident on an unexpected command.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(string FileName, string[] Arguments, Func<Task<ProcessResult>> Answer)> _script = new();
    private readonly List<ProcessSpec> _calls = new();

    /// <summary>Everything run so far, oldest first.</summary>
    public IReadOnlyList<ProcessSpec> Calls => _calls;

    /// <summary>Answers <paramref name="fileName"/> with exactly <paramref name="arguments"/> by <paramref name="result"/>.</summary>
    public FakeProcessRunner On(string fileName, string[] arguments, ProcessResult result)
    {
        _script.Add((fileName, arguments, () => Task.FromResult(result)));
        return this;
    }

    /// <summary>Shorthand for a successful run printing <paramref name="stdout"/>.</summary>
    public FakeProcessRunner On(string fileName, string[] arguments, string stdout) =>
        On(fileName, arguments, new ProcessResult(0, stdout, ""));

    /// <summary>Makes the call fail with <paramref name="exception"/>, as a timeout or a missing program would.</summary>
    public FakeProcessRunner On(string fileName, string[] arguments, Exception exception)
    {
        _script.Add((fileName, arguments, () => Task.FromException<ProcessResult>(exception)));
        return this;
    }

    /// <inheritdoc />
    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        _calls.Add(spec);
        var entry = _script.FirstOrDefault(s =>
            string.Equals(s.FileName, spec.FileName, StringComparison.OrdinalIgnoreCase) && s.Arguments.SequenceEqual(spec.Arguments));
        if (entry.Answer is null)
        {
            throw new InvalidOperationException($"Unscripted process: {spec.FileName} {string.Join(" ", spec.Arguments)}");
        }
        return entry.Answer();
    }

    /// <inheritdoc />
    public IRunningProcess Start(ProcessSpec spec) =>
        throw new NotSupportedException("FakeProcessRunner only scripts runs to completion.");
}
