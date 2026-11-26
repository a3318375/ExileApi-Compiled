using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MenuZhCN;

// Upstream uses the visible breadcrumb as a favorite's persisted identifier.
// Translate only known paths on this property and keep the English form on disk.
public sealed class FavoritesConverter : JsonConverter
{
    static readonly Dictionary<string, string> EnglishToChinese = LoadMap();
    static readonly Dictionary<string, string> ChineseToEnglish = EnglishToChinese
        .ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    static Dictionary<string, string> LoadMap()
    {
        using var stream = typeof(FavoritesConverter).Assembly.GetManifestResourceStream("Favorites.json")!;
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    public override bool CanConvert(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition().FullName == "ExileCore.Shared.Nodes.ContentNode`1" &&
        type.GenericTypeArguments[0].FullName == "ExileCore.Shared.Nodes.TextNode";

    static void Translate(JToken token, Dictionary<string, string> paths)
    {
        if (token is not JObject root || root.GetValue("Content", StringComparison.OrdinalIgnoreCase) is not JArray entries) return;
        foreach (var node in entries.OfType<JObject>())
        {
            var value = node.Property("Value", StringComparison.OrdinalIgnoreCase);
            if (value?.Value.Type == JTokenType.String && paths.TryGetValue((string)value.Value!, out var text))
                value.Value = text;
        }
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        if (token.Type == JTokenType.Null) return null;
        Translate(token, EnglishToChinese);
        if (existingValue is null) return token.ToObject(objectType, serializer);
        using var source = token.CreateReader();
        serializer.Populate(source, existingValue);
        return existingValue;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null) { writer.WriteNull(); return; }
        var token = JObject.FromObject(value, serializer);
        Translate(token, ChineseToEnglish);
        token.WriteTo(writer);
    }
}
