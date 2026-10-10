# External toolchain pins. The .NET SDK pin lives in global.json.
@{
    Dpm = @{
        # Digital Asset publishes no checksums for these archives. The hashes below were
        # taken when each version was adopted; a mismatch means the file changed upstream
        # and needs a human look before re-pinning.
        BaseUrl = 'https://get.digitalasset.com/install/dpm-sdk'
        Sdks    = @(
            # First entry is the default SDK and the one dpm itself is bootstrapped from.
            @{ Version = '3.6.0'; Sha256 = 'a826f5733c80ae7763ef4631fe0d8a82a64345a10dbcbe117e9bcc76883db8c5' }
            @{ Version = '3.5.2'; Sha256 = 'e4881c56ff1db28e9999dcbac3ff72d61905e70bcfc2253331d7836c3d043e7d' }
        )
    }

    Java = @{ MinimumMajor = 17 }

    # Oldest VS 2022 we test against; VS 2026 is always the newest installed 18.x.
    VsTestHosts = @('17.14', '18')
}
