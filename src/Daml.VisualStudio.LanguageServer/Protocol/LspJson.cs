using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Daml.VisualStudio.LanguageServer.Protocol;

/// <summary>
/// Parses and writes message bodies the adapter has to look into. Strings that happen to look
/// like dates stay strings, so a message written back out carries exactly what came in.
/// </summary>
internal static class LspJson
{
    /// <summary>The body as an object, or null when it isn't JSON (which isn't ours to judge).</summary>
    public static JObject? Parse(byte[] body)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(body))) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The compact UTF-8 bytes of a message.</summary>
    public static byte[] ToBytes(JToken message) => Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
}
