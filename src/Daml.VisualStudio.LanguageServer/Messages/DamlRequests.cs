using System;
using System.Threading.Tasks;
using Daml.VisualStudio.LanguageServer.Hosting;

namespace Daml.VisualStudio.LanguageServer.Messages;

/// <summary>damlc's own requests and notifications sent by the client.</summary>
public static class DamlRequests
{
    /// <summary>
    /// Sends <c>daml/sdkInstallCancel {sdkVersionIdentifier}</c> on the current session's
    /// connection. Returns false when no session is connected.
    /// </summary>
    public static async Task<bool> CancelSdkInstallAsync(this IDamlRpcChannel channel, string sdkVersionIdentifier)
    {
        if (channel is null)
        {
            throw new ArgumentNullException(nameof(channel));
        }
        if (!channel.TryGet(out var rpc, out _))
        {
            return false;
        }
        await rpc.NotifyWithParameterObjectAsync("daml/sdkInstallCancel", new { sdkVersionIdentifier }).ConfigureAwait(false);
        return true;
    }
}
