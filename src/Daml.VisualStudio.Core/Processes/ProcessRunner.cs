using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Daml.VisualStudio.Core.Processes;

/// <summary><see cref="IProcessRunner"/> over <see cref="Process"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        if (spec is null)
        {
            throw new ArgumentNullException(nameof(spec));
        }
        cancellationToken.ThrowIfCancellationRequested();

        using var process = RunningProcess.Start(spec);
        process.StandardInput.Dispose();
        var stdout = ReadAllAsync(process.StandardOutput);
        var stderr = ReadAllAsync(process.StandardError);
        var finished = Task.WhenAll(process.Exited, stdout, stderr);

        var limit = spec.Timeout;
        using var timeout = limit is { } span ? new CancellationTokenSource(span) : new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stopped = Task.Delay(Timeout.Infinite, stop.Token);
        if (await Task.WhenAny(finished, stopped).ConfigureAwait(false) != finished)
        {
            process.KillTree();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{spec.FileName} didn't finish within {limit?.TotalSeconds:0.#} s and was stopped.");
        }

        return new ProcessResult(await process.Exited.ConfigureAwait(false), await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <inheritdoc />
    public IRunningProcess Start(ProcessSpec spec) =>
        RunningProcess.Start(spec ?? throw new ArgumentNullException(nameof(spec)));

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private sealed class RunningProcess : IRunningProcess
    {
        private readonly Process _process;
        private readonly DateTime _startTime;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private RunningProcess(Process process)
        {
            _process = process;
            _process.Exited += (_, _) => _exited.TrySetResult(_process.ExitCode);
            _process.Start();
            _startTime = _process.StartTime;
            Id = _process.Id;
        }

        public int Id { get; }

        public Stream StandardInput => _process.StandardInput.BaseStream;

        public Stream StandardOutput => _process.StandardOutput.BaseStream;

        public Stream StandardError => _process.StandardError.BaseStream;

        public Task<int> Exited => _exited.Task;

        public static RunningProcess Start(ProcessSpec spec)
        {
            if (Path.IsPathRooted(spec.FileName) && !File.Exists(spec.FileName))
            {
                throw new FileNotFoundException($"{spec.FileName} doesn't exist.", spec.FileName);
            }

            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = spec.WorkingDirectory ?? "",
            };
            if (CommandLine.IsBatchFile(spec.FileName))
            {
                startInfo.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                startInfo.Arguments = CommandLine.ForBatch(spec.FileName, spec.Arguments);
            }
            else
            {
                startInfo.FileName = spec.FileName;
                startInfo.Arguments = CommandLine.ForExecutable(spec.Arguments);
            }
            foreach (var variable in spec.Environment)
            {
                if (variable.Value is null)
                {
                    startInfo.Environment.Remove(variable.Key);
                }
                else
                {
                    startInfo.Environment[variable.Key] = variable.Value;
                }
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            try
            {
                return new RunningProcess(process);
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        public void KillTree() => ProcessTree.Kill(_process, _startTime);

        public void Dispose() => _process.Dispose();
    }
}
