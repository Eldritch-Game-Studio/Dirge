using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dirge.Profiling;

/// <summary>
/// JSON form of a <see cref="Recording"/>: camelCase, enums as names, lists populated in place (the recording types expose
/// get-only lists), floats written round-trip exact. Independent of <see cref="Dirge.Serialization.ProjectJson"/> so a
/// recording file never needs the project schema.
/// </summary>
public static class RecordingJson
{
    private static readonly JsonSerializerOptions Options = Create(indented: true);
    private static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string Serialize(Recording recording, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(recording);
        return JsonSerializer.Serialize(recording, indented ? Options : Compact);
    }

    public static Recording Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<Recording>(json, Options) ?? throw new JsonException("Recording document is null.");
    }
}
