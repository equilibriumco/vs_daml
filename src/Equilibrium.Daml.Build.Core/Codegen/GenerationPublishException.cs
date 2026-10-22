using System;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>A publish that didn't happen; the message says what state the generation was left in.</summary>
public sealed class GenerationPublishException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">EQD0104 when the previous generation is in place, EQD0109 when it is kept for the next build to restore.</param>
    /// <param name="message">What happened, for people.</param>
    /// <param name="inner">The failure behind it.</param>
    public GenerationPublishException(string code, string message, Exception inner)
        : base(message, inner) => Code = code;

    /// <summary>The diagnostic code to report.</summary>
    public string Code { get; }
}
