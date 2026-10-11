using BenchmarkDotNet.Running;

namespace Daml.VisualStudio.Benchmarks;

internal static class Program
{
    // BenchmarkDotNet's own command line: --filter, --runtimes, --job, ...
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
