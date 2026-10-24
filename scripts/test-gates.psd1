# Test layout and gate definitions for scripts/test.ps1.
@{
    # Which test projects hold which category. test.ps1 launches only these, and refuses to run
    # when a test project under test/ is listed nowhere, so a new project can't silently drop out.
    Projects = @{
        Unit        = @('Equilibrium.Daml.Build.Tests', 'Equilibrium.Daml.VisualStudio.Core.Tests', 'Equilibrium.Daml.VisualStudio.LanguageServer.Tests', 'Equilibrium.Daml.VisualStudio.Roslyn.Tests', 'TestHelpers.Tests')
        # Tests that start real processes (the fake dpm, the fake language server, cmd.exe) and
        # wait on them: kept out of Unit, which stays fast and deterministic.
        Process     = @('Equilibrium.Daml.Build.Tests', 'Equilibrium.Daml.VisualStudio.Core.Tests', 'Equilibrium.Daml.VisualStudio.LanguageServer.Tests', 'TestHelpers.Tests')
        Editor      = @('Equilibrium.Daml.VisualStudio.Tests')
        Contract    = @('Equilibrium.Daml.VisualStudio.Core.Tests', 'Equilibrium.Daml.VisualStudio.LanguageServer.Tests')
        MSBuild     = @('Equilibrium.Daml.Build.Tests')
        Integration = @('Equilibrium.Daml.VisualStudio.IntegrationTests')
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
            Required = @('Unit', 'Process', 'Editor', 'MSBuild')
            Pending  = @{}
            Tools    = @()
            Coverage = @{}
        }
        M0 = @{
            Required = @('Unit', 'Process', 'Editor', 'Integration')
            Pending  = @{}
            Tools    = @()
            Coverage = @{
                'net10.0' = @{
                    'Equilibrium.Daml.VisualStudio.Core'           = 85
                    'Equilibrium.Daml.VisualStudio.LanguageServer' = 80
                    'Equilibrium.Daml.Build.Core'                  = 80
                }
            }
        }
        # M1: the language server against the real damlc (Contract, SDK 3.6 and 3.5) and the
        # extension inside Visual Studio (Integration) join the gate; both need dpm.
        M1 = @{
            Required = @('Unit', 'Process', 'Editor', 'Contract', 'Integration')
            Pending  = @{}
            Tools    = @('dpm')
            Coverage = @{
                'net10.0' = @{
                    'Equilibrium.Daml.VisualStudio.Core'           = 85
                    'Equilibrium.Daml.VisualStudio.LanguageServer' = 80
                    'Equilibrium.Daml.Build.Core'                  = 80
                }
            }
        }
        # M2: what M1 requires, now also covering the native editor and the script results; the
        # demo is the human's part of the gate.
        M2 = @{
            Required = @('Unit', 'Process', 'Editor', 'Contract', 'Integration')
            Pending  = @{}
            Tools    = @('dpm')
            Coverage = @{
                'net10.0' = @{
                    'Equilibrium.Daml.VisualStudio.Core'           = 85
                    'Equilibrium.Daml.VisualStudio.LanguageServer' = 80
                    'Equilibrium.Daml.Build.Core'                  = 80
                }
            }
        }
        # Hosted: the most a GitHub-hosted runner can prove, for the Nightly workflow. It has no
        # Visual Studio session (Integration) and no dpm (Contract, real MSBuild), so the coverage
        # of the tasks, which those runs give, is left to M3/M6 on a machine that has them.
        Hosted = @{
            Required = @('Unit', 'Process', 'Editor', 'MSBuild')
            Pending  = @{}
            Tools    = @()
            Coverage = @{
                'net10.0' = @{
                    'Equilibrium.Daml.VisualStudio.Core'           = 85
                    'Equilibrium.Daml.VisualStudio.LanguageServer' = 80
                    'Equilibrium.Daml.Build.Core'                  = 80
                }
                'net48'   = @{
                    'Equilibrium.Daml.VisualStudio.Roslyn' = 75
                }
            }
        }
        # M3: Daml projects in MSBuild and the C# bindings join the gate. L4 runs against the real
        # dpm and generator too (MSBuildBackend), so the JDK is required; the tasks and the Roslyn
        # index are measured on net48, the framework Visual Studio loads them in.
        M3 = @{
            Required       = @('Unit', 'Process', 'Editor', 'Contract', 'MSBuild', 'Integration')
            Pending        = @{}
            Tools          = @('dpm', 'java')
            MSBuildBackend = 'All'
            Coverage       = @{
                'net10.0' = @{
                    'Equilibrium.Daml.VisualStudio.Core'           = 85
                    'Equilibrium.Daml.VisualStudio.LanguageServer' = 80
                    'Equilibrium.Daml.Build.Core'                  = 80
                }
                'net48'   = @{
                    'Equilibrium.Daml.Build.Tasks'         = 80
                    'Equilibrium.Daml.VisualStudio.Roslyn' = 75
                }
            }
        }
        # M6: the 1.0 release on the lexer. Every category of M3 again, run on each supported host
        # with the same tools and thresholds; the grammar stays its own path (M4).
        M6 = @{
            Required       = @('Unit', 'Process', 'Editor', 'Contract', 'MSBuild', 'Integration')
            Pending        = @{}
            Tools          = @('dpm', 'java')
            MSBuildBackend = 'All'
            Coverage       = @{
                'net10.0' = @{
                    'Equilibrium.Daml.VisualStudio.Core'           = 85
                    'Equilibrium.Daml.VisualStudio.LanguageServer' = 80
                    'Equilibrium.Daml.Build.Core'                  = 80
                }
                'net48'   = @{
                    'Equilibrium.Daml.Build.Tasks'         = 80
                    'Equilibrium.Daml.VisualStudio.Roslyn' = 75
                }
            }
        }
        # M4: the tree-sitter grammar on its own (it doesn't touch the extension): corpus,
        # highlight and tag tests, `tree-sitter fuzz`, the scripts' tests and the corpus gate,
        # which in a gate run needs the collected real-world corpus.
        M4 = @{
            Required = @('Grammar')
            Pending  = @{}
            Tools    = @()
            Coverage = @{}
        }
    }
}
