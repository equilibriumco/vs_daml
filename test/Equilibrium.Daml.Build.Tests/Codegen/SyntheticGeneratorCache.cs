using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Equilibrium.Daml.Build.Core.Configuration;

namespace Equilibrium.Daml.Build.Tests.Codegen;

/// <summary>
/// A generator cache laid out the way dpm leaves it in a private home: the pinned OCI index and
/// the windows/amd64 manifest as blobs named by their SHA-256, and the component's files
/// unpacked under cache\components. Small files stand in for the real ones; every digest and
/// size is computed, so verification sees what it would see on a real machine.
/// </summary>
internal sealed class SyntheticGeneratorCache
{
    public const string Repository = "ghcr.io/peacefulstudio/dpm-codegen-cs";

    public static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>
    {
        ["component.yaml"] = "name: dpm-codegen-cs\n",
        ["dpm-codegen-cs.cmd"] = "@echo off\r\n",
        ["bin/Daml.Codegen.CSharp.Cli.exe"] = "MZ emitter",
        ["bin/daml-dar-to-proto.jar"] = "PK jar of the DAR reader",
    };

    private SyntheticGeneratorCache(string root, CodegenComponentPin pin, string manifestDigest)
    {
        Root = root;
        Pin = pin;
        ManifestDigest = manifestDigest;
    }

    public string Root { get; }

    public CodegenComponentPin Pin { get; }

    public string ManifestDigest { get; }

    public string Home => Path.Combine(Root, Pin.Digest);

    public string Blobs => Path.Combine(Home, @"cache\oci-layout\blobs\sha256");

    public string Component => Path.Combine(Home, @"cache\components", Repository.Replace('/', '\\'));

    /// <summary>
    /// Builds a complete cache under <paramref name="root"/>; <paramref name="windows"/> false
    /// leaves the Windows build out of the index, and another <paramref name="variant"/> makes
    /// another component, so another pin. <paramref name="contents"/> replaces the component's
    /// files; one named outside the component is listed in the manifest but never written.
    /// </summary>
    public static SyntheticGeneratorCache Create(string root, bool windows = true, string variant = "", IReadOnlyDictionary<string, string>? contents = null)
    {
        var files = (contents ?? Files).ToDictionary(f => f.Key, f => f.Key == "component.yaml" ? f.Value + variant : f.Value);
        var layers = files.Select(f =>
        {
            var bytes = Encoding.UTF8.GetBytes(f.Value);
            var title = f.Key.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $$$"""{"mediaType":"application/vnd.component.file","digest":"sha256:{{{Sha256(bytes)}}}","size":{{{bytes.Length}}},"annotations":{"org.opencontainers.image.title":"{{{title}}}"}}""";
        });
        var manifest = Encoding.UTF8.GetBytes(
            $$"""{"schemaVersion":2,"mediaType":"application/vnd.oci.image.manifest.v1+json","layers":[{{string.Join(",", layers)}}]}""");
        var manifestDigest = Sha256(manifest);
        var platforms = new List<string>
        {
            """{"mediaType":"application/vnd.oci.image.manifest.v1+json","digest":"sha256:344afcc702ec7d3469944f71243769c086340294d0832614d646f58c5e3c938d","size":2863,"platform":{"os":"darwin","architecture":"arm64"}}""",
        };
        if (windows)
        {
            platforms.Add($$$"""{"mediaType":"application/vnd.oci.image.manifest.v1+json","digest":"sha256:{{{manifestDigest}}}","size":{{{manifest.Length}}},"platform":{"os":"windows","architecture":"amd64"}}""");
        }
        var index = Encoding.UTF8.GetBytes(
            $$"""
            {
              "schemaVersion": 2,
              "mediaType": "application/vnd.oci.image.index.v1+json",
              "manifests": [{{string.Join(",\n    ", platforms)}}]
            }
            """);
        var pin = CodegenComponentPin.TryParse($"oci://{Repository}:0.6.0@sha256:{Sha256(index)}")!;

        var cache = new SyntheticGeneratorCache(root, pin, manifestDigest);
        Directory.CreateDirectory(cache.Blobs);
        File.WriteAllBytes(Path.Combine(cache.Blobs, pin.Digest), index);
        File.WriteAllBytes(Path.Combine(cache.Blobs, manifestDigest), manifest);
        foreach (var file in files.Where(f => !Path.IsPathRooted(f.Key) && !f.Key.Split('/', '\\').Contains("..")))
        {
            var path = Path.Combine(cache.Component, file.Key.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value);
        }
        return cache;
    }

    public string ComponentFile(string name) => Path.Combine(Component, name.Replace('/', '\\'));

    /// <summary>Copies this home into <paramref name="home"/>, as a download of the pin into another dpm home would leave it.</summary>
    public void CopyHomeTo(string home)
    {
        foreach (var file in Directory.GetFiles(Home, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(home, file.Substring(Home.Length + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static string Sha256(byte[] bytes)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(bytes);
        return string.Concat(sha.GetHashAndReset().Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
