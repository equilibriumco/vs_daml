using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

namespace Equilibrium.Daml.VisualStudio.Benchmarks;

internal static class Program
{
    // BenchmarkDotNet's own command line: --filter, --runtimes, --job, ...
    // The budgets are on medians, so the median goes into every report, and the brief JSON is
    // what bench.ps1 -Check reads.
    private static void Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddColumn(StatisticColumn.Median)
            .AddExporter(JsonExporter.Brief);
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
