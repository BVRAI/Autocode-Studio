using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoCode.Engine.Llm;

public static class JsonHelpers
{
    public static Dictionary<string, object?> JsonStringToDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return ToDictionary(doc.RootElement);
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_raw"] = json
            };
        }
    }

    public static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = ToObject(prop.Value);
        }

        return result;
    }

    public static object? ToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => ToDictionary(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var i) ? i : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };

    public static JsonNode? ToJsonNode(object? value) =>
        value switch
        {
            null => null,
            JsonNode node => JsonNode.Parse(node.ToJsonString()),
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            float f => JsonValue.Create(f),
            decimal m => JsonValue.Create(m),
            IEnumerable<object?> items => new JsonArray(items.Select(ToJsonNode).ToArray()),
            Dictionary<string, object?> dict => DictionaryToJson(dict),
            IReadOnlyDictionary<string, object?> dict => DictionaryToJson(dict),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
        };

    public static JsonObject DictionaryToJson(IReadOnlyDictionary<string, object?> dict)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in dict)
        {
            obj[key] = ToJsonNode(value);
        }

        return obj;
    }

    public static JsonNode CloneNode(JsonNode node) => JsonNode.Parse(node.ToJsonString())!;

    public static string SerializeObject(object? value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
}
