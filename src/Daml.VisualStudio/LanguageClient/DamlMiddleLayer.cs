using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Microsoft.VisualStudio.LanguageServer.Client;
using Newtonsoft.Json.Linq;

namespace Daml.VisualStudio.LanguageClient;

/// <summary>
/// Passes every message through unchanged and, at Debug level, logs which methods VS calls and
/// how long requests take. Messages are fixed up on the byte stream instead, because
/// <c>initialize</c> never reaches a middle layer.
/// </summary>
internal sealed class DamlMiddleLayer : ILanguageClientMiddleLayer2<JToken>
{
    private readonly IDamlLogger _logger;

    public DamlMiddleLayer(IDamlLogger logger) => _logger = logger;

    public bool CanHandle(string methodName) => _logger.IsEnabled(DamlLogLevel.Debug);

    public Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification)
    {
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Debug, $"notify {methodName}");
        return sendNotification(methodParam);
    }

    public async Task<JToken?> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken?>> sendRequest)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await sendRequest(methodParam).ConfigureAwait(false);
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Debug, $"request {methodName}: {clock.ElapsedMilliseconds} ms");
            return result;
        }
        catch (Exception ex)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Debug, $"request {methodName} failed after {clock.ElapsedMilliseconds} ms: {ex.Message}");
            throw;
        }
    }
}
