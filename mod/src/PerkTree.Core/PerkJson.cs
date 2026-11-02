using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerkTree.Core;

/// <summary>Single place that defines how tree/effect/save files are read and written.</summary>
public static class PerkJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            // Hand-edited data files: let authors leave comments and trailing commas in.
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    public static TreeDef ParseTree(string json) =>
        JsonSerializer.Deserialize<TreeDef>(json, Options) ?? throw new JsonException("Tree document is empty.");

    public static EffectCatalog ParseCatalog(string json) =>
        JsonSerializer.Deserialize<EffectCatalog>(json, Options) ?? throw new JsonException("Effect catalog is empty.");

    public static TreeDef LoadTreeFile(string path) => ParseTree(File.ReadAllText(path));

    public static EffectCatalog LoadCatalogFile(string path) => ParseCatalog(File.ReadAllText(path));

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
