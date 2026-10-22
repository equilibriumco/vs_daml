using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Composition;

namespace Equilibrium.Daml.VisualStudio.Tests;

// The extension's MEF parts as Visual Studio would discover them, without composing them.
internal static class MefParts
{
    public static async Task<DiscoveredParts> DiscoverAsync()
    {
        var discovery = PartDiscovery.Combine(
            new AttributedPartDiscoveryV1(Resolver.DefaultInstance),
            new AttributedPartDiscovery(Resolver.DefaultInstance, isNonPublicSupported: true));
        return await discovery.CreatePartsAsync(typeof(DamlPackage).Assembly);
    }

    public static async Task<IEnumerable<ExportDefinition>> ExportsAsync(Type contract) =>
        ExportsOf(await DiscoverAsync(), contract);

    public static IEnumerable<ExportDefinition> ExportsOf(DiscoveredParts parts, Type contract) =>
        parts.Parts.SelectMany(p => p.ExportingMembers.SelectMany(m => m.Value).Concat(p.ExportedTypes))
            .Where(e => e.ContractName == ContractName(contract));

    public static List<ComposablePartDefinition> PartsExporting(DiscoveredParts parts, Type contract) =>
        parts.Parts.Where(p => p.ExportedTypes.Any(e => e.ContractName == ContractName(contract))).ToList();

    // MEF's contract names: the full type name, with generic arguments in parentheses.
    public static string ContractName(Type type) =>
        type.IsGenericType
            ? $"{type.GetGenericTypeDefinition().FullName!.Split('`')[0]}({string.Join(",", type.GetGenericArguments().Select(ContractName))})"
            : type.FullName!;
}
