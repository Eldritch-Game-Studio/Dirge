#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Godot.Collections;

namespace Dirge.Tool.Mixer;

internal enum EffectParameterKind { Float, Int, Bool, Enum, String }

/// <summary>One editable engine property of an <c>AudioEffect</c> class.</summary>
internal sealed class EffectParameter
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public EffectParameterKind Kind { get; init; }
    public float Min { get; init; }
    public float Max { get; init; }
    public float Step { get; init; } = 0.01f;
    public string[] EnumLabels { get; init; } = System.Array.Empty<string>();
    public float DefaultValue { get; init; }
    public string DefaultString { get; init; } = "";
}

/// <summary>
/// Reflection over the engine's audio effect classes: available types (built-in <c>AudioEffect</c> inheritors plus the Dirge
/// native effects when their extension is loaded) and per-type parameter descriptions from
/// <c>ClassDB.ClassGetPropertyList</c>, honouring <see cref="PropertyHint.Range"/> and <see cref="PropertyHint.Enum"/>.
/// </summary>
internal static class EffectCatalog
{
    private static readonly string[] NativeTypes = { "DirgeAudioEffectSend", "DirgeAudioEffectReturn", "DirgeAudioEffectConvolutionReverb" };
    private static readonly System.Collections.Generic.Dictionary<string, EffectParameter[]> Cache = new();
    private static readonly System.Collections.Generic.Dictionary<string, AudioEffect?> Defaults = new();

    public static List<string> AvailableTypes()
    {
        var types = new List<string>();
        string[] inheritors = ClassDB.GetInheritersFromClass(nameof(AudioEffect));
        for (int i = 0; i < inheritors.Length; i++)
        {
            string type = inheritors[i];
            if (!ClassDB.CanInstantiate(type)) continue;
            if (type == nameof(AudioEffectCapture) || type == nameof(AudioEffectSpectrumAnalyzer) || type == nameof(AudioEffectRecord)) continue;
            if (System.Array.IndexOf(NativeTypes, type) >= 0) continue;
            types.Add(type);
        }
        types.Sort(StringComparer.Ordinal);
        for (int i = 0; i < NativeTypes.Length; i++)
        {
            if (ClassDB.ClassExists(NativeTypes[i])) types.Add(NativeTypes[i]);
        }
        return types;
    }

    /// <summary>"AudioEffectLowPassFilter" → "Low Pass Filter"; "DirgeAudioEffectSend" → "Dirge Send".</summary>
    public static string DisplayName(string typeName)
    {
        string name = typeName;
        bool native = name.StartsWith("Dirge", StringComparison.Ordinal);
        if (native) name = name.Substring(5);
        if (name.StartsWith("AudioEffect", StringComparison.Ordinal)) name = name.Substring("AudioEffect".Length);
        if (name.Length == 0) name = typeName;
        var text = new System.Text.StringBuilder(name.Length + 8);
        if (native) text.Append("Dirge ");
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1])) text.Append(' ');
            text.Append(c);
        }
        return text.ToString();
    }

    public static EffectParameter[] Parameters(string typeName)
    {
        if (Cache.TryGetValue(typeName, out EffectParameter[]? cached)) return cached;
        EffectParameter[] parameters = Describe(typeName);
        Cache[typeName] = parameters;
        return parameters;
    }

    private static EffectParameter[] Describe(string typeName)
    {
        if (!ClassDB.ClassExists(typeName)) return System.Array.Empty<EffectParameter>();
        AudioEffect? defaults = DefaultInstance(typeName);
        var result = new List<EffectParameter>();
        // Walk the class and its AudioEffect-derived ancestors so subclasses (e.g. filters) show inherited parameters.
        var chain = new List<string>();
        for (string current = typeName; current.Length > 0 && current != nameof(AudioEffect) && current != nameof(Resource); current = ClassDB.GetParentClass(current))
        {
            chain.Add(current);
        }
        for (int c = chain.Count - 1; c >= 0; c--)
        {
            Array<Dictionary> list = ClassDB.ClassGetPropertyList(chain[c], true);
            foreach (Dictionary property in list)
            {
                EffectParameter? parameter = Convert(property, defaults);
                if (parameter is not null) result.Add(parameter);
            }
        }
        return result.ToArray();
    }

    private static EffectParameter? Convert(Dictionary property, AudioEffect? defaults)
    {
        string name = property["name"].AsString();
        var usage = (PropertyUsageFlags)property["usage"].AsInt64();
        if ((usage & PropertyUsageFlags.Editor) == 0 || (usage & (PropertyUsageFlags.Group | PropertyUsageFlags.Subgroup | PropertyUsageFlags.Category)) != 0) return null;
        if (name == "resource_name" || name == "resource_path" || name == "resource_local_to_scene" || name == "script") return null;
        var type = (Variant.Type)property["type"].AsInt64();
        var hint = (PropertyHint)property["hint"].AsInt64();
        string hintString = property["hint_string"].AsString();
        string label = Label(name);
        Variant current = defaults is not null ? defaults.Get(name) : default;

        switch (type)
        {
            case Variant.Type.Float:
            case Variant.Type.Int when hint != PropertyHint.Enum:
            {
                bool isInt = type == Variant.Type.Int;
                float min = isInt ? 0f : -1f, max = isInt ? 100f : 1f, step = isInt ? 1f : 0.01f;
                if (hint == PropertyHint.Range) ParseRange(hintString, isInt, ref min, ref max, ref step);
                else if (!isInt) GuessRange(name, ref min, ref max, ref step);
                float value = current.VariantType == Variant.Type.Nil ? 0f : isInt ? current.AsInt32() : current.AsSingle();
                if (hint != PropertyHint.Range)
                {
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
                return new EffectParameter { Name = name, Label = label, Kind = isInt ? EffectParameterKind.Int : EffectParameterKind.Float, Min = min, Max = max, Step = step, DefaultValue = value };
            }
            case Variant.Type.Int:
            {
                string[] labels = ParseEnum(hintString);
                float value = current.VariantType == Variant.Type.Nil ? 0f : current.AsInt32();
                return new EffectParameter { Name = name, Label = label, Kind = EffectParameterKind.Enum, Min = 0, Max = Math.Max(0, labels.Length - 1), Step = 1f, EnumLabels = labels, DefaultValue = value };
            }
            case Variant.Type.Bool:
                return new EffectParameter { Name = name, Label = label, Kind = EffectParameterKind.Bool, Min = 0, Max = 1, Step = 1f, DefaultValue = current.VariantType == Variant.Type.Nil ? 0f : current.AsBool() ? 1f : 0f };
            case Variant.Type.String:
            case Variant.Type.StringName:
                return new EffectParameter { Name = name, Label = label, Kind = EffectParameterKind.String, DefaultString = current.VariantType == Variant.Type.Nil ? "" : current.AsString() };
            default:
                return null;
        }
    }

    private static void ParseRange(string hintString, bool isInt, ref float min, ref float max, ref float step)
    {
        string[] parts = hintString.Split(',');
        if (parts.Length > 0 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float a)) min = a;
        if (parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float b)) max = b;
        if (parts.Length > 2 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s > 0f) step = s;
        else step = isInt ? 1f : DefaultStep(max - min);
        if (max < min) (min, max) = (max, min);
    }

    private static float DefaultStep(float span) => span > 1000f ? 1f : span > 20f ? 0.1f : span > 2f ? 0.01f : 0.001f;

    private static void GuessRange(string name, ref float min, ref float max, ref float step)
    {
        if (name.EndsWith("_db", StringComparison.Ordinal)) { min = -80f; max = 24f; step = 0.1f; }
        else if (name.EndsWith("_hz", StringComparison.Ordinal)) { min = 1f; max = 20500f; step = 1f; }
        else if (name.EndsWith("_ms", StringComparison.Ordinal)) { min = 0f; max = 2000f; step = 1f; }
        else { min = 0f; max = 1f; step = 0.01f; }
    }

    private static string[] ParseEnum(string hintString)
    {
        if (hintString.Length == 0) return System.Array.Empty<string>();
        string[] parts = hintString.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            int colon = parts[i].IndexOf(':');
            parts[i] = (colon >= 0 ? parts[i].Substring(0, colon) : parts[i]).Trim();
        }
        return parts;
    }

    private static string Label(string property)
    {
        var text = new System.Text.StringBuilder(property.Length);
        bool upper = true;
        for (int i = 0; i < property.Length; i++)
        {
            char c = property[i];
            if (c == '_' || c == '/') { text.Append(' '); upper = true; continue; }
            text.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return text.ToString();
    }

    private static AudioEffect? DefaultInstance(string typeName)
    {
        if (Defaults.TryGetValue(typeName, out AudioEffect? cached)) return cached;
        AudioEffect? instance = null;
        if (ClassDB.ClassExists(typeName) && ClassDB.CanInstantiate(typeName) && ClassDB.IsParentClass(typeName, nameof(AudioEffect)))
        {
            instance = ClassDB.Instantiate(typeName).AsGodotObject() as AudioEffect;
        }
        Defaults[typeName] = instance;
        return instance;
    }
}
