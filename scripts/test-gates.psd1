# Test layout and gate definitions for scripts/test.ps1.
@{
    # Which test projects hold which category. test.ps1 launches only these, and refuses to run
    # when a test project under test/ is listed nowhere, so a new project can't silently drop out.
    Projects = @{
        Unit        = @('Daml.Build.Tests', 'Daml.VisualStudio.Core.Tests', 'Daml.VisualStudio.LanguageServer.Tests', 'TestHelpers.Tests')
        Editor      = @('Daml.VisualStudio.Tests')
        Contract    = @('Daml.VisualStudio.Core.Tests')
        MSBuild     = @('Daml.Build.Tests')
        Integration = @('Daml.VisualStudio.IntegrationTests')
    }

    # What a gate run (-RequiredTools) must prove:
    #   Required - every project mapped to the category must execute at least one test on each
    #              target framework it builds for, with no failures and no skips.
    #   Pending  - categories that exist but deliberately have no tests yet; they still run and
    #              show up as N/A with the reason, and become an error once they gain tests
    #              without being moved to Required.
    #   Tools    - external tools that must be found.
    #   Coverage - minimum line coverage per framework and product assembly, computed from the
    #              runs that executed tests. An assembly without coverable lines passes as
    #              "no code"; one missing from the reports fails.
    # PR is the pull-request/CI profile: required tests are enforced, coverage is only reported.
    Gates = @{
        PR = @{
            Required = @('Unit', 'Editor')
            Pending  = @{ MSBuild = 'MSBuild tests arrive with the MSBuild packages' }
            Tools    = @()
            Coverage = @{}
        }
        M0 = @{
            Required = @('Unit', 'Editor', 'Integration')
            Pending  = @{}
            Tools    = @()
            Coverage = @{
                'net10.0' = @{
                    'Daml.VisualStudio.Core'           = 85
                    'Daml.VisualStudio.LanguageServer' = 80
                    'Daml.Build.Core'                  = 80
                }
            }
        }
    }
}
