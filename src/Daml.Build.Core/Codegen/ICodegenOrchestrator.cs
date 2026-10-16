using System.Threading;

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
    CodegenResult Run(CodegenRequest request, CancellationToken cancellationToken);
}
