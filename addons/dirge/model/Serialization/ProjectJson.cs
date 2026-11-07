using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Dirge.Project;

namespace Dirge.Serialization;

/// <summary>
/// Text format for <see cref="AudioProject"/>: System.Text.Json, camelCase keys, enums as names, deterministic key order
/// (<c>id</c>, <c>name</c>/<c>path</c>, then the remaining properties ordinally sorted; dictionaries sorted by key).
/// The project types stay attribute-free; every contract rule lives in <see cref="Options"/>.
///
/// Polymorphic lists (<see cref="Instrument"/>, <see cref="Modulator"/>, <see cref="LogicMarker"/>) carry a <c>$type</c>
/// discriminator: the class name with its <c>Instrument</c>/<c>Modulator</c>/<c>Marker</c>/<c>Region</c> suffix removed
/// where that stays unambiguous.
/// <code>
/// Instrument:   ClipInstrument → Clip, MultiInstrument → Multi, ScattererInstrument → Scatterer,
///               SilenceInstrument → Silence, EventInstrument → Event, ProgrammerInstrument → Programmer,
///               CommandInstrument → Command, SnapshotInstrument → Snapshot
/// Modulator:    RandomModulator → Random, AhdsrModulator → Ahdsr, LfoModulator → Lfo, AutopitchModulator → Autopitch
/// LogicMarker:  DestinationMarker → Destination, LoopRegion → Loop, TransitionMarker → Transition,
///               TransitionRegion → TransitionRegion (stripping "Region" would collide with TransitionMarker),
///               SustainPoint → SustainPoint (no suffix to strip), MagnetRegion → Magnet
/// </code>
/// Value encodings: <see cref="DirgeId"/> ↔ 32-hex string, <see cref="AssetRef"/> ↔ string,
/// <see cref="CurvePoint"/> ↔ <c>[x, y, "Shape"]</c>, <see cref="PropertyRef"/> ↔ <c>{kind, owner, effectIndex?, name?}</c>.
/// </summary>
public static class ProjectJson
{
    public const string TypeDiscriminator = "$type";

    public static JsonSerializerOptions Options { get; } = CreateOptions(indented: true);

    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(indented: false);

    public static string Serialize(AudioProject project, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, indented ? Options : CompactOptions);
    }

    public static AudioProject Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<AudioProject>(json, Options)
               ?? throw new JsonException("Project document is null.");
    }

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            IncludeFields = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ApplyContract } },
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new DirgeIdConverter());
        options.Converters.Add(new AssetRefConverter());
        options.Converters.Add(new PropertyRefConverter());
        options.Converters.Add(new CurvePointConverter());
        options.Converters.Add(new SortedStringDictionaryConverterFactory());
        return options;
    }

    // ---- contract modifier -------------------------------------------------------------------------------------

    private static void ApplyContract(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;

        ApplyPolymorphism(info);

        // Remove computed read-only scalars (e.g. Placement.OnTimeline); give get-only dictionaries a copying setter
        // so the sorted dictionary converter can feed them; get-only lists are populated in place.
        for (int i = info.Properties.Count - 1; i >= 0; i--)
        {
            JsonPropertyInfo property = info.Properties[i];
            if (property.Set is not null || property.Get is null) continue;
            Type type = property.PropertyType;
            if (typeof(IDictionary).IsAssignableFrom(type))
            {
                Func<object, object?> getter = property.Get;
                property.Set = (target, value) =>
                {
                    var destination = (IDictionary)getter(target)!;
                    destination.Clear();
                    if (value is IDictionary source)
                        foreach (DictionaryEntry entry in source) destination[entry.Key] = entry.Value;
                };
            }
            else if (!typeof(IEnumerable).IsAssignableFrom(type))
            {
                info.Properties.RemoveAt(i);
            }
        }

        var ordered = new List<JsonPropertyInfo>(info.Properties);
        ordered.Sort(static (a, b) =>
        {
            int rank = Rank(a.Name).CompareTo(Rank(b.Name));
            return rank != 0 ? rank : string.CompareOrdinal(a.Name, b.Name);
        });
        for (int i = 0; i < ordered.Count; i++) ordered[i].Order = i;

        static int Rank(string name) => name switch
        {
            "id" => 0,
            "name" or "path" => 1,
            _ => 2,
        };
    }

    private static void ApplyPolymorphism(JsonTypeInfo info)
    {
        JsonPolymorphismOptions? polymorphism = null;
        if (info.Type == typeof(Instrument))
        {
            polymorphism = Polymorphism(
                (typeof(ClipInstrument), "Clip"),
                (typeof(MultiInstrument), "Multi"),
                (typeof(ScattererInstrument), "Scatterer"),
                (typeof(SilenceInstrument), "Silence"),
                (typeof(EventInstrument), "Event"),
                (typeof(ProgrammerInstrument), "Programmer"),
                (typeof(CommandInstrument), "Command"),
                (typeof(SnapshotInstrument), "Snapshot"));
        }
        else if (info.Type == typeof(Modulator))
        {
            polymorphism = Polymorphism(
                (typeof(RandomModulator), "Random"),
                (typeof(AhdsrModulator), "Ahdsr"),
                (typeof(LfoModulator), "Lfo"),
                (typeof(AutopitchModulator), "Autopitch"));
        }
        else if (info.Type == typeof(LogicMarker))
        {
            polymorphism = Polymorphism(
                (typeof(DestinationMarker), "Destination"),
                (typeof(LoopRegion), "Loop"),
                (typeof(TransitionMarker), "Transition"),
                (typeof(TransitionRegion), "TransitionRegion"),
                (typeof(SustainPoint), "SustainPoint"),
                (typeof(MagnetRegion), "Magnet"));
        }
        if (polymorphism is not null) info.PolymorphismOptions = polymorphism;
    }

    private static JsonPolymorphismOptions Polymorphism(params (Type Type, string Discriminator)[] derived)
    {
        var options = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = TypeDiscriminator,
            IgnoreUnrecognizedTypeDiscriminators = false,
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
        };
        foreach ((Type type, string discriminator) in derived)
            options.DerivedTypes.Add(new JsonDerivedType(type, discriminator));
        return options;
    }

    // ---- value converters --------------------------------------------------------------------------------------

    private sealed class DirgeIdConverter : JsonConverter<DirgeId>
    {
        public override DirgeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("DirgeId must be a 32-hex string.");
            string text = reader.GetString()!;
            if (text.Length != 32 || !Guid.TryParseExact(text, "N", out Guid guid))
                throw new JsonException($"'{text}' is not a 32-hex DirgeId.");
            return new DirgeId(guid);
        }

        public override void Write(Utf8JsonWriter writer, DirgeId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value.ToString("N"));
    }

    private sealed class AssetRefConverter : JsonConverter<AssetRef>
    {
        public override AssetRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return new AssetRef("");
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("AssetRef must be a string.");
            return new AssetRef(reader.GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, AssetRef value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Path ?? "");
    }

    private sealed class PropertyRefConverter : JsonConverter<PropertyRef>
    {
        public override PropertyRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("PropertyRef must be an object.");
            PropertyKind kind = default;
            bool hasKind = false;
            DirgeId owner = DirgeId.None;
            int effectIndex = -1;
            string? name = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string key = reader.GetString()!;
                reader.Read();
                switch (key)
                {
                    case "kind":
                        string kindText = reader.GetString() ?? "";
                        if (!Enum.TryParse(kindText, ignoreCase: false, out kind)) throw new JsonException($"Unknown PropertyKind '{kindText}'.");
                        hasKind = true;
                        break;
                    case "owner":
                        owner = JsonSerializer.Deserialize<DirgeId>(ref reader, options);
                        break;
                    case "effectIndex":
                        effectIndex = reader.GetInt32();
                        break;
                    case "name":
                        name = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                        break;
                    default:
                        throw new JsonException($"Unknown PropertyRef member '{key}'.");
                }
            }
            if (!hasKind) throw new JsonException("PropertyRef is missing 'kind'.");
            return new PropertyRef(kind, owner, effectIndex, name);
        }

        public override void Write(Utf8JsonWriter writer, PropertyRef value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", value.Kind.ToString());
            writer.WritePropertyName("owner");
            JsonSerializer.Serialize(writer, value.Owner, options);
            if (value.EffectIndex != -1) writer.WriteNumber("effectIndex", value.EffectIndex);
            if (value.Name is not null) writer.WriteString("name", value.Name);
            writer.WriteEndObject();
        }
    }

    private sealed class CurvePointConverter : JsonConverter<CurvePoint>
    {
        public override CurvePoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("CurvePoint must be [x, y, shape].");
            reader.Read();
            float x = reader.GetSingle();
            reader.Read();
            float y = reader.GetSingle();
            reader.Read();
            CurveShape shape = CurveShape.Linear;
            if (reader.TokenType != JsonTokenType.EndArray)
            {
                string shapeText = reader.GetString() ?? "";
                if (!Enum.TryParse(shapeText, ignoreCase: false, out shape)) throw new JsonException($"Unknown CurveShape '{shapeText}'.");
                reader.Read();
            }
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("CurvePoint must be [x, y, shape].");
            return new CurvePoint(x, y, shape);
        }

        public override void Write(Utf8JsonWriter writer, CurvePoint value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteStringValue(value.ShapeToNext.ToString());
            writer.WriteEndArray();
        }
    }

    /// <summary>Writes <c>Dictionary&lt;string, T&gt;</c> with ordinally sorted keys so re-saves do not reorder entries.</summary>
    private sealed class SortedStringDictionaryConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert.IsGenericType
               && typeToConvert.GetGenericTypeDefinition() == typeof(Dictionary<,>)
               && typeToConvert.GetGenericArguments()[0] == typeof(string);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            Type valueType = typeToConvert.GetGenericArguments()[1];
            return (JsonConverter)Activator.CreateInstance(typeof(SortedStringDictionaryConverter<>).MakeGenericType(valueType))!;
        }
    }

    private sealed class SortedStringDictionaryConverter<TValue> : JsonConverter<Dictionary<string, TValue>>
    {
        public override Dictionary<string, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected an object.");
            var result = new Dictionary<string, TValue>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string key = reader.GetString()!;
                reader.Read();
                result[key] = JsonSerializer.Deserialize<TValue>(ref reader, options)!;
            }
            return result;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, TValue> value, JsonSerializerOptions options)
        {
            var keys = new List<string>(value.Keys);
            keys.Sort(StringComparer.Ordinal);
            writer.WriteStartObject();
            foreach (string key in keys)
            {
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, value[key], options);
            }
            writer.WriteEndObject();
        }
    }
}
