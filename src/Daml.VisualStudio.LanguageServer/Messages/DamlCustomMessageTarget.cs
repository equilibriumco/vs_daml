using System;
using System.Diagnostics.CodeAnalysis;
using Daml.VisualStudio.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Daml.VisualStudio.LanguageServer.Messages;

/// <summary>
/// Receives damlc's own <c>daml/*</c> notifications on Visual Studio's connection and hands them
/// on, typed, to the sinks. A notification that doesn't have the expected shape is logged and
/// dropped rather than failing the connection.
/// </summary>
[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "JSON-RPC dispatches to instance methods of the target.")]
public sealed class DamlCustomMessageTarget
{
    private readonly IVirtualResourceSink _virtualResources;
    private readonly ISdkInstallProgressSink _sdkInstalls;
    private readonly IDamlLogger _logger;

    /// <summary>Creates the target.</summary>
    public DamlCustomMessageTarget(IVirtualResourceSink virtualResources, ISdkInstallProgressSink sdkInstalls, IDamlLogger logger)
    {
        _virtualResources = virtualResources ?? throw new ArgumentNullException(nameof(virtualResources));
        _sdkInstalls = sdkInstalls ?? throw new ArgumentNullException(nameof(sdkInstalls));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary><c>daml/virtualResource/didChange {uri, contents}</c>.</summary>
    [JsonRpcMethod("daml/virtualResource/didChange", UseSingleObjectParameterDeserialization = true)]
    public void VirtualResourceDidChange(JToken parameters)
    {
        if (Text(parameters, "uri") is { } uri && Text(parameters, "contents") is { } contents)
        {
            _virtualResources.Changed(new VirtualResourceChangedParams(uri, contents));
            return;
        }
        Malformed("daml/virtualResource/didChange", parameters);
    }

    /// <summary><c>daml/virtualResource/note {uri, note}</c>.</summary>
    [JsonRpcMethod("daml/virtualResource/note", UseSingleObjectParameterDeserialization = true)]
    public void VirtualResourceNote(JToken parameters)
    {
        if (Text(parameters, "uri") is { } uri && Text(parameters, "note") is { } note)
        {
            _virtualResources.Noted(new VirtualResourceNoteParams(uri, note));
            return;
        }
        Malformed("daml/virtualResource/note", parameters);
    }

    /// <summary><c>daml/virtualResource/didProgress {uri, millisecondsPassed, startedAt}</c>.</summary>
    [JsonRpcMethod("daml/virtualResource/didProgress", UseSingleObjectParameterDeserialization = true)]
    public void VirtualResourceDidProgress(JToken parameters)
    {
        if (Text(parameters, "uri") is { } uri && Number(parameters, "millisecondsPassed") is { } passed && Number(parameters, "startedAt") is { } started)
        {
            _virtualResources.Progressed(new VirtualResourceProgressParams(uri, passed, started));
            return;
        }
        Malformed("daml/virtualResource/didProgress", parameters);
    }

    /// <summary><c>daml/sdkInstallProgress {sdkVersionIdentifier, sdkVersionRendered, kind, progress}</c>.</summary>
    [JsonRpcMethod("daml/sdkInstallProgress", UseSingleObjectParameterDeserialization = true)]
    public void SdkInstallProgress(JToken parameters)
    {
        var kind = Text(parameters, "kind") switch
        {
            "begin" => SdkInstallProgressKind.Begin,
            "report" => SdkInstallProgressKind.Report,
            "end" => SdkInstallProgressKind.End,
            _ => (SdkInstallProgressKind?)null,
        };
        if (Text(parameters, "sdkVersionIdentifier") is { } id && kind is { } stage)
        {
            var rendered = Text(parameters, "sdkVersionRendered") ?? id;
            var progress = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Number(parameters, "progress") ?? 0));
            _sdkInstalls.Report(new SdkInstallProgressParams(id, rendered, stage, progress));
            return;
        }
        Malformed("daml/sdkInstallProgress", parameters);
    }

    private static string? Text(JToken parameters, string name) =>
        parameters is JObject fields && fields[name] is JValue { Type: JTokenType.String } value ? (string?)value : null;

    private static long? Number(JToken parameters, string name) =>
        parameters is JObject fields && fields[name] is JValue { Type: JTokenType.Integer or JTokenType.Float } value ? (long)(double)value : null;

    private void Malformed(string method, JToken parameters) =>
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, $"Ignored a {method} notification with unexpected parameters: {parameters.ToString(Formatting.None)}");
}
