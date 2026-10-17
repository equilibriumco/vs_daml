using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Codegen;

namespace Daml.Build.Tests.Tasks;

// Records what a task asks of the orchestrator and answers with a fixed result.
internal sealed class FakeOrchestrator : ICodegenOrchestrator
{
    public List<CodegenRequest> Requests { get; } = [];

    public List<CodegenRequest> Cleans { get; } = [];

    public List<CancellationToken> Tokens { get; } = [];

    public CodegenResult Result { get; set; } = new([], []);

    public Action? OnRun { get; set; }

    public Task<CodegenResult> RunAsync(CodegenRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Answer(cancellationToken);
    }

    public Task<CodegenResult> CleanAsync(CodegenRequest request, CancellationToken cancellationToken)
    {
        Cleans.Add(request);
        return Answer(cancellationToken);
    }

    private Task<CodegenResult> Answer(CancellationToken cancellationToken)
    {
        Tokens.Add(cancellationToken);
        OnRun?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result);
    }
}
