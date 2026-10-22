$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $here '..\..\scripts\lib\DesignTimeBudget.psm1') -Force

# The message of what $Script throws, or null. Pester 3.4's `Should Throw` misses every exception
# under PowerShell 7 (it works in Windows PowerShell 5.1), so the tests catch it themselves.
function Get-ThrownMessage([scriptblock] $Script) {
    try {
        & $Script | Out-Null
        return $null
    }
    catch {
        return $_.Exception.Message
    }
}

Describe 'Read-DesignTimeRuns' {
    It 'reads the ticks of every run from the driver lines, in run order' {
        $lines = @(
            'Build started.',
            '  EQDAML-DTB-RUN 1 4300000',
            '  EQDAML-DTB-RUN 0 6970000',
            'warning: something else',
            '  EQDAML-DTB-RUN 2 523456'
        )

        $runs = Read-DesignTimeRuns $lines

        @($runs).Count | Should Be 3
        $runs[0] | Should Be 697
        $runs[1] | Should Be 430
        $runs[2] | Should Be 52.3456
    }

    It 'refuses output without runs or with a run twice' {
        Get-ThrownMessage { Read-DesignTimeRuns @('Build succeeded.') } | Should Match 'no design-time runs'
        Get-ThrownMessage { Read-DesignTimeRuns @('EQDAML-DTB-RUN 0 10', 'EQDAML-DTB-RUN 0 20') } | Should Match 'run 0 twice'
    }
}

Describe 'Get-Median' {
    It 'takes the middle value, or the mean of the two middle ones' {
        Get-Median @(5, 1, 3) | Should Be 3
        Get-Median @(4, 1, 3, 2) | Should Be 2.5
    }
}

Describe 'Measure-DesignTimeOverhead' {
    It 'keeps the first run of each project as the cold one and the median of the rest as the warm one' {
        $result = Measure-DesignTimeOverhead -Baseline @(430, 33, 36, 38, 39, 41) -WithDaml @(697, 54, 58, 59, 63, 69)

        $result.ColdBaselineMs | Should Be 430
        $result.ColdWithDamlMs | Should Be 697
        $result.ColdOverheadMs | Should Be 267
        $result.WarmBaselineMs | Should Be 38
        $result.WarmWithDamlMs | Should Be 59
        $result.WarmOverheadMs | Should Be 21
        $result.WarmRuns | Should Be 5
    }

    It 'needs a warm run besides the cold one, and the same number for both projects' {
        Get-ThrownMessage { Measure-DesignTimeOverhead -Baseline @(430) -WithDaml @(697) } | Should Match 'at least one warm run'
        Get-ThrownMessage { Measure-DesignTimeOverhead -Baseline @(430, 40) -WithDaml @(697, 60, 61) } | Should Match 'same number of runs'
    }
}

Describe 'Test-DesignTimeBudget' {
    It 'passes a warm overhead within the budget, whatever the cold one' {
        $check = Test-DesignTimeBudget ([pscustomobject] @{ WarmOverheadMs = 21; ColdOverheadMs = 267 })

        $check.Passed | Should Be $true
        $check.BudgetMs | Should Be 200
        $check.Message | Should Match '21 ms'
    }

    It 'fails a warm overhead over the budget and says by how much' {
        $check = Test-DesignTimeBudget ([pscustomobject] @{ WarmOverheadMs = 250.4; ColdOverheadMs = 400 }) -BudgetMs 200

        $check.Passed | Should Be $false
        $check.Message | Should Match '250 ms'
        $check.Message | Should Match '200 ms'
    }

    It 'refuses a result without a warm overhead' {
        Get-ThrownMessage { Test-DesignTimeBudget ([pscustomobject] @{ ColdOverheadMs = 267 }) } | Should Match 'WarmOverheadMs'
    }
}
