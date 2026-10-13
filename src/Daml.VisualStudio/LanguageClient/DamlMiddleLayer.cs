using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Microsoft.VisualStudio.LanguageServer.Client;
using Newtonsoft.Json.Linq;

namespace Daml.VisualStudio.LanguageClient;

/// <summary>
/// Sits between VS and the server for everything after <c>initialize</c>. It keeps messages
/// about documents outside the workspace from reaching damlc (notifications are dropped,
/// requests get an empty answer), drops configuration changes damlc has no handler for, and, at
/// Debug level, logs what VS calls and how long it takes.
/// Message content is fixed up on the byte stream instead, because <c>initialize</c> never
/// reaches a middle layer.
/// </summary>
internal sealed class DamlMiddleLayer : ILanguageClientMiddleLayer2<JToken>
{
    private const string DidChangeConfiguration = "workspace/didChangeConfiguration";

    private readonly IDamlLogger _logger;
    private readonly Func<string?, bool> _isServed;
    private readonly Func<JToken?, JToken?> _redirectDefinitions;
    private readonly ConcurrentDictionary<string, bool> _reportedOutside = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="logger">Where skipped documents and Debug traffic are logged.</param>
    /// <param name="isServed">Whether a document URI belongs to the workspace the server runs.</param>
    /// <param name="redirectDefinitions">Rewrites definition answers, e.g. to files Visual Studio can open; none if null.</param>
    public DamlMiddleLayer(IDamlLogger logger, Func<string?, bool> isServed, Func<JToken?, JToken?>? redirectDefinitions = null)
    {
        _logger = logger;
        _isServed = isServed;
        _redirectDefinitions = redirectDefinitions ?? (result => result);
    }

    public bool CanHandle(string methodName) =>
        methodName.StartsWith("textDocument/", StringComparison.Ordinal)
        || methodName == DidChangeConfiguration
        || _logger.IsEnabled(DamlLogLevel.Debug);

    public Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification)
    {
        // damlc answers it with an error in its log; its settings come on the command line.
        if (methodName == DidChangeConfiguration || !Served(methodParam))
        {
            return Task.CompletedTask;
        }
        if (_logger.IsEnabled(DamlLogLevel.Debug))
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Debug, $"notify {methodName}");
        }
        return sendNotification(methodParam);
    }

    public async Task<JToken?> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken?>> sendRequest)
    {
        if (!Served(methodParam))
        {
            return null;
        }
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await sendRequest(methodParam).ConfigureAwait(false);
            if (IsDefinitionRequest(methodName))
            {
                result = _redirectDefinitions(result);
            }
            if (_logger.IsEnabled(DamlLogLevel.Debug))
            {
                _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Debug, $"request {methodName}: {clock.ElapsedMilliseconds} ms");
            }
            return result;
        }
        catch (Exception ex) when (_logger.IsEnabled(DamlLogLevel.Debug))
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Debug, $"request {methodName} failed after {clock.ElapsedMilliseconds} ms: {ex.Message}");
            throw;
        }
    }

    private static bool IsDefinitionRequest(string methodName) =>
        methodName is "textDocument/definition" or "textDocument/declaration" or "textDocument/typeDefinition" or "textDocument/implementation";

    private bool Served(JToken? methodParam)
    {
        var uri = (methodParam as JObject)?["textDocument"]?["uri"]?.Value<string>();
        if (_isServed(uri))
        {
            return true;
        }
        if (uri is not null && _reportedOutside.TryAdd(uri, true))
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information,
                $"{uri} is outside the Daml workspace; it gets syntax colouring only. Open its folder for language features.");
        }
        return false;
    }
}
