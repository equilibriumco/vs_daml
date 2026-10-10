# What a milestone gate must actually run. With -RequiredTools every category listed
# here has to execute at least one test, with no failures and no skips, and every tool
# has to be present. A milestone is added once its test projects exist.
@{
    M0 = @{
        Categories = @('Unit', 'Integration')
        Tools      = @()
    }
}
