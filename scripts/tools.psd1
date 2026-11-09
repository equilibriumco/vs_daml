# External toolchain pins. The .NET SDK pin lives in global.json.
@{
    Dpm = @{
        # Digital Asset publishes no checksums for these archives. The hashes below were
        # taken when each version was adopted; a mismatch means the file changed upstream
        # and needs a human look before re-pinning.
        BaseUrl = 'https://get.digitalasset.com/install/dpm-sdk'
        # The C# generator component (dpm codegen-cs) is documented to need at least this dpm.
        MinimumVersion = '1.0.20'
        Sdks    = @(
            # First entry is the default SDK and the one dpm itself is bootstrapped from.
            @{ Version = '3.7.1'; Sha256 = '18abfd25d465a7717c43705850e31b4216cf9e0984c82c3191718fad1add3b1d' }
            @{ Version = '3.6.4'; Sha256 = '9310a02c5d646786484cee94a67bd7fa73890aab46ec396b2af9fc7ce0c45580' }
        )
    }

    Java = @{ MinimumMajor = 17 }
}
