using System.Threading;
using Daml.Build.Core.Building;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Diagnostics;

namespace Daml.Build.Tasks;

// Stands in until the tasks ship a generator: a project with Daml references must fail with a
// reason rather than compile without its bindings.
internal sealed class UnavailableCodegenOrchestrator : ICodegenOrchestrator
{
    public CodegenResult Run(CodegenRequest request, CancellationToken cancellationToken) => new(
        [],
        [new BuildMessage(DamlDiagnosticSeverity.Error, DamlDiagnosticCodes.CodegenFailed, null, request.ProjectPath, 0, 0, 0, 0,
            "Generating C# from DARs isn't available in this version of the Daml build tasks.")]);
}
