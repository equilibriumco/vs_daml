# Where the real-world Daml corpus for the grammar comes from (scripts/collect-corpus.ps1).
# Every source is pinned to a commit; Paths are the folders whose .daml files are taken. The
# corpus itself (grammar/test/real-world/) isn't committed: it is collected again from these pins.
@{
    MinimumFiles = 500

    Sources = @(
        @{
            Name        = 'daml'
            Repository  = 'https://github.com/digital-asset/daml'
            Ref         = 'main'
            Commit      = '682ba2eabde9e48a2acc12f859115c8b79e6145d'
            Sdk         = '3.x (main, after 3.4.11)'
            License     = 'Apache-2.0'
            LicenseFile = 'LICENSE'
            # The standard library and its primitives, the Daml Script library and the code of
            # the documentation. The compiler's own tests are left out: many are meant not to parse.
            Paths       = @(
                'sdk/compiler/damlc/daml-stdlib-src'
                'sdk/compiler/damlc/daml-prim-src'
                'sdk/daml-script/daml'
                'sdk/docs'
            )
        }
        @{
            Name        = 'daml-finance'
            Repository  = 'https://github.com/digital-asset/daml-finance'
            Ref         = 'main'
            Commit      = '5ba1463ba09a9bdc9267a25ae135b25900351121'
            Sdk         = '2.10.0'
            License     = 'Apache-2.0'
            LicenseFile = 'LICENSE'
            Paths       = @(
                'src'
                'docs'
            )
        }
        @{
            Name        = 'splice'
            Repository  = 'https://github.com/hyperledger-labs/splice'
            Ref         = '0.9.1'
            Commit      = 'ec6ec6f618d1a9d75a74954f30458240109ba275'
            Sdk         = '3.5.2'
            License     = 'Apache-2.0'
            LicenseFile = 'LICENSE'
            Paths       = @(
                'daml'
                'token-standard'
            )
        }
        @{
            # This repository's own samples, taken from the working tree (Local: no fetch).
            Name        = 'samples'
            Local       = $true
            Sdk         = '3.6.0'
            License     = 'Apache-2.0'
            LicenseFile = 'LICENSE'
            Paths       = @(
                'samples'
            )
        }
    )
}
