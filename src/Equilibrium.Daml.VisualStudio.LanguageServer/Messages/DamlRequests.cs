using System;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;
using StreamJsonRpc;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Messages;

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

    /// <summary>
    /// Sends <c>daml/keepAlive</c>, with no parameters; damlc answers <c>null</c> for as long as
    /// it is responsive.
    /// </summary>
    public static Task KeepAliveAsync(this JsonRpc rpc, CancellationToken cancellationToken)
    {
        if (rpc is null)
        {
            throw new ArgumentNullException(nameof(rpc));
        }
        return rpc.InvokeWithCancellationAsync<object?>("daml/keepAlive", null, cancellationToken);
    }
}
