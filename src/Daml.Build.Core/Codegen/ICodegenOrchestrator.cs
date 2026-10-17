using System.Threading;
using System.Threading.Tasks;

namespace Daml.Build.Core.Codegen;

/// <summary>Keeps a project's generated C# in step with the DARs it references.</summary>
public interface ICodegenOrchestrator
{
    /// <summary>
    /// Brings every generation of <paramref name="request"/> up to date, unless it is a
    /// design-time request, and lists the files to compile. Generations of references the
    /// project no longer has drop out of the list.
    /// </summary>
    /// <param name="request">The project, its settings and its inputs.</param>
    /// <param name="cancellationToken">Stops the run; whatever was published before stays consistent.</param>
    /// <exception cref="System.OperationCanceledException">The run was cancelled.</exception>
    Task<CodegenResult> RunAsync(CodegenRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Removes what the project generated under its root, and nothing the generator didn't
    /// write. The request's inputs don't matter: every generation of the project goes.
    /// </summary>
    /// <param name="request">The project and its settings.</param>
    /// <param name="cancellationToken">Stops waiting for another build.</param>
    /// <exception cref="System.OperationCanceledException">The clean was cancelled.</exception>
    Task<CodegenResult> CleanAsync(CodegenRequest request, CancellationToken cancellationToken);
}
