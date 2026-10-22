using System;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Processes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Equilibrium.Daml.VisualStudio.Core.Sdk;

/// <summary>
/// Reads dpm's active SDK from <c>dpm versions -o json</c>, which lists only installed SDKs
/// (<c>dpm version --all</c> would ask the remote registry). The active SDK is dpm's global
/// default, not necessarily what a package's daml.yaml asks for.
/// </summary>
public sealed class DpmSdkResolver : ISdkResolver
{
    private static readonly string[] Arguments = ["versions", "-o", "json"];

    private readonly IProcessRunner _runner;

    /// <summary>Creates a resolver running dpm through <paramref name="runner"/>.</summary>
    public DpmSdkResolver(IProcessRunner runner) =>
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <inheritdoc />
    public AssistantKind Kind => AssistantKind.Dpm;

    /// <inheritdoc />
    public async Task<string?> ReadSdkVersionAsync(AssistantLocation assistant, string workingDirectory, CancellationToken cancellationToken)
    {
        var output = await VersionCommand.RunAsync(_runner, assistant, workingDirectory, Arguments, cancellationToken).ConfigureAwait(false);
        return ParseActiveVersion(output);
    }

    /// <summary>The <c>version</c> of the entry marked <c>active</c>, or <c>null</c> if none is.</summary>
    /// <exception cref="FormatException">The text isn't a list of version entries.</exception>
    public static string? ParseActiveVersion(string json)
    {
        JToken root;
        try
        {
            root = JToken.Parse(json);
        }
        catch (JsonReaderException ex)
        {
            throw new FormatException($"'dpm versions -o json' printed something other than JSON: {Excerpt(json)}", ex);
        }
        if (root is not JArray entries)
        {
            throw new FormatException($"'dpm versions -o json' printed JSON that isn't a list: {Excerpt(json)}");
        }

        foreach (var entry in entries)
        {
            if (entry is not JObject fields || fields["version"] is not JValue { Type: JTokenType.String } version)
            {
                throw new FormatException($"'dpm versions -o json' printed an entry without a version string: {Excerpt(entry.ToString(Formatting.None))}");
            }
            if (fields["active"] is JValue { Type: JTokenType.Boolean } active && (bool)active)
            {
                return (string?)version;
            }
        }
        return null;
    }

    private static string Excerpt(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed.Substring(0, 200) + "…";
    }
}
