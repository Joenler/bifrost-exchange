using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bifrost.Quoter.Schedule;

/// <summary>
/// File loader for <see cref="Scenario"/> definitions. Uses snake_case JSON
/// property naming and kebab-upper-case enum values (CALM, TRENDING, VOLATILE,
/// SHOCK) so the on-disk shape matches the sibling <c>scenario.schema.json</c>.
/// </summary>
public static class ScenarioLoader
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseUpper) }
    };

    /// <summary>
    /// Reads and deserialises a scenario file from disk. Throws on parse
    /// failure or when the file is empty / contains <c>null</c>.
    /// </summary>
    public static Scenario Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return JsonSerializer.Deserialize<Scenario>(bytes, Opts)
            ?? throw new InvalidOperationException($"Scenario parse returned null: {path}");
    }
}
