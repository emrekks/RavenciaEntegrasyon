using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// Golden contract tests (ADAPTOR-DIGER-10, ADAPTOR-TY-HB-9): a marketplace payload kept under Fixtures/<Marketplace>/
// goes through the adapter's public mapper and the mapped contract object is compared with a checked-in golden file
// next to it. Fixture files may start with "//" lines naming where their shape comes from (doc file, endpoint); those
// lines are dropped before the payload is handed to a mapper (XML fixtures use an XML comment instead).
//
// A golden is (re)written only when MARKETPLACEHUB_UPDATE_GOLDENS=1 is set; otherwise a missing or different golden
// fails the test with a readable diff. After regenerating, read the golden diff before committing it.
internal static class ContractFixture
{
    public const string UpdateVariable = "MARKETPLACEHUB_UPDATE_GOLDENS";

    // Raw echoes of the remote payload: kept out of the golden as a short hash so the golden stays readable but a
    // change in what is echoed still shows.
    private static readonly HashSet<string> RawEchoProperties = new(StringComparer.Ordinal)
    {
        "RawJson", "SourceSnapshotJson", "RawPayloadJson", "RawXml", "RawPayload", "SourcePayloadJson"
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    // The payload text of Fixtures/<relativePath> (copied to the test output), without its leading "//" source lines.
    public static string Read(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Fixture missing from the test output: {path}");
        var lines = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join('\n', lines.SkipWhile(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    public static JsonDocument Json(string relativePath) => JsonDocument.Parse(Read(relativePath));

    public static XElement Xml(string relativePath) => XElement.Parse(Read(relativePath));

    // Serializes the mapped value deterministically and compares it with Fixtures/<goldenPath>.
    public static void MatchesGolden(string goldenPath, object? value, [CallerFilePath] string callerFile = "")
    {
        var actual = Serialize(value);
        var sourcePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFile)!, "..", "Fixtures", goldenPath.Replace('/', Path.DirectorySeparatorChar)));
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, actual, new UTF8Encoding(false));
            return;
        }
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", goldenPath.Replace('/', Path.DirectorySeparatorChar));
        var expectedPath = File.Exists(outputPath) ? outputPath : sourcePath;
        Assert.True(File.Exists(expectedPath), $"Golden {goldenPath} is missing. Run the test once with {UpdateVariable}=1, then review the written file.");
        var expected = File.ReadAllText(expectedPath, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
        Assert.Fail($"Mapped contract differs from golden {goldenPath} (set {UpdateVariable}=1 to accept a deliberate change):\n{Diff(expected, actual)}");
    }

    // The mapped value, or what the mapper refused it with, so a refusal is pinned in the golden like a result.
    public static object? Outcome(Func<object?> map)
    {
        try { return map(); }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            return new { Refused = exception.GetType().Name, exception.Message };
        }
    }

    public static string Serialize(object? value)
    {
        var node = JsonSerializer.SerializeToNode(value, value?.GetType() ?? typeof(object), Options);
        node = Normalize(node, null);
        return (node?.ToJsonString(Options) ?? "null").Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static JsonNode? Normalize(JsonNode? node, string? propertyName)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                    obj[key] = Normalize(obj[key]?.DeepClone(), key);
                return obj;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                    array[index] = Normalize(array[index]?.DeepClone(), null);
                return array;
            case JsonValue value when propertyName is not null && value.TryGetValue<string>(out var text):
                if (RawEchoProperties.Contains(propertyName))
                    return JsonValue.Create(Fingerprint(text));
                if (propertyName.EndsWith("Json", StringComparison.Ordinal) && TryParse(text) is { } parsed)
                    return Normalize(parsed, null);
                return value;
            default:
                return node;
        }
    }

    private static JsonNode? TryParse(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '[')) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static string Fingerprint(string text)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
        return $"<raw {text.Length} chars sha256:{hash}>";
    }

    private static string Diff(string expected, string actual)
    {
        var left = expected.Split('\n');
        var right = actual.Split('\n');
        var first = 0;
        while (first < left.Length && first < right.Length && left[first] == right[first]) first++;
        var builder = new StringBuilder();
        builder.AppendLine($"first difference at line {first + 1}");
        for (var index = Math.Max(0, first - 3); index < Math.Min(left.Length, first + 8); index++)
            builder.AppendLine($"- {index + 1,5}: {left[index]}");
        for (var index = Math.Max(0, first - 3); index < Math.Min(right.Length, first + 8); index++)
            builder.AppendLine($"+ {index + 1,5}: {right[index]}");
        return builder.ToString();
    }
}
