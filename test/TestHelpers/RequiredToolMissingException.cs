using System;

namespace Daml.TestHelpers;

/// <summary>Raised in gate runs when a test needs a tool that isn't installed.</summary>
public sealed class RequiredToolMissingException : Exception
{
    /// <summary>Creates the exception with a message naming the missing tool.</summary>
    public RequiredToolMissingException(string message)
        : base(message)
    {
    }
}
