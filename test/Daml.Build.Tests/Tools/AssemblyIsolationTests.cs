#if NETFRAMEWORK
using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Reflection;
using Daml.Build.Core.Codegen;
using Microsoft.CSharp;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Tools;

public class AssemblyIsolationTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public void A_legacy_SDK_copy_cannot_satisfy_the_extensions_Core_reference(bool legacyFirst)
    {
        var root = Path.Combine(Path.GetTempPath(), "daml-assembly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var legacy = Path.Combine(root, "Daml.Build.Core.dll");
        try
        {
            using var compiler = new CSharpCodeProvider();
            var result = compiler.CompileAssemblyFromSource(new CompilerParameters
            {
                GenerateExecutable = false,
                OutputAssembly = legacy,
            }, """
                [assembly: System.Reflection.AssemblyVersion("0.3.0.0")]
                namespace Daml.Build.Core.Codegen { public class DarProvenance { } }
                """);
            result.Errors.HasErrors.ShouldBeFalse();
            var domain = AppDomain.CreateDomain("Daml isolation", null, new AppDomainSetup { ApplicationBase = AppDomain.CurrentDomain.BaseDirectory });
            try
            {
                var probe = (Probe)domain.CreateInstanceAndUnwrap(typeof(Probe).Assembly.FullName, typeof(Probe).FullName);
                probe.Check(legacy, typeof(DarProvenance).Assembly.Location, legacyFirst).ShouldBeTrue();
            }
            finally { AppDomain.Unload(domain); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public sealed class Probe : MarshalByRefObject
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "The instance call crosses into the isolated AppDomain.")]
        public bool Check(string legacy, string current, bool legacyFirst)
        {
            var first = Assembly.LoadFrom(legacyFirst ? legacy : current);
            var second = Assembly.LoadFrom(legacyFirst ? current : legacy);
            var expected = legacyFirst ? second : first;
            var resolved = Assembly.Load(AssemblyName.GetAssemblyName(current));
            return first.GetName().GetPublicKeyToken().Length != second.GetName().GetPublicKeyToken().Length
                && expected.FullName == resolved.FullName
                && expected.ManifestModule.ModuleVersionId == resolved.ManifestModule.ModuleVersionId
                && resolved.GetType("Daml.Build.Core.Codegen.DarProvenance")?.GetProperty("Modules") is not null;
        }
    }
}
#endif
