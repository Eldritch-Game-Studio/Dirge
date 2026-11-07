#nullable enable
using System.Collections.Generic;
using Dirge.Project;
using Godot;

namespace Dirge.Tool.Events;

/// <summary>Natural (unplaced) lengths of instruments for display: asset durations from the project catalog or the engine, nested event lengths.</summary>
public sealed class AssetLengthCache
{
    private readonly Dictionary<string, double> _lengths = new();
    private readonly EventEditorState _state;

    public const double DefaultLength = 1.0;
    public const double CommandLength = 0.05;

    public AssetLengthCache(EventEditorState state) => _state = state;

    public void Clear() => _lengths.Clear();

    public double AssetLength(AssetRef asset)
    {
        if (asset.IsEmpty) return 0;
        if (_lengths.TryGetValue(asset.Path, out double cached)) return cached;
        double seconds = 0;
        if (_state.Project.AssetLengths.TryGetValue(asset.Path, out double known)) seconds = known;
        else if (ResourceLoader.Exists(asset.Path))
        {
            var stream = ResourceLoader.Load<AudioStream>(asset.Path);
            seconds = stream is null ? 0 : stream.GetLength();
        }
        _lengths[asset.Path] = seconds;
        return seconds;
    }

    /// <summary>Length in seconds an instrument occupies on the timeline when <see cref="Placement.Length"/> is 0.</summary>
    public double NaturalLength(Instrument instrument, int depth = 0)
    {
        switch (instrument)
        {
            case ClipInstrument clip:
                return Positive(AssetLength(clip.Asset));
            case MultiInstrument multi:
                return Positive(MaxEntry(multi.Entries));
            case ScattererInstrument scatterer:
                return Positive(MaxEntry(scatterer.Entries));
            case EventInstrument nested:
                return Positive(EventLength(_state.FindEvent(nested.Event), depth + 1));
            case CommandInstrument:
                return CommandLength;
            default:
                return DefaultLength;
        }
    }

    public double EventLength(AudioEvent? ev, int depth = 0)
    {
        if (ev is null || depth > 8) return 0;
        double length = 0;
        for (int t = 0; t < ev.Tracks.Count; t++)
        {
            List<Instrument> list = ev.Tracks[t].Instruments;
            for (int i = 0; i < list.Count; i++)
            {
                Instrument inst = list[i];
                if (!inst.Placement.OnTimeline) continue;
                double len = inst.Placement.Length > 0 ? inst.Placement.Length : NaturalLength(inst, depth);
                if (inst.Placement.Start + len > length) length = inst.Placement.Start + len;
            }
        }
        for (int i = 0; i < ev.Markers.Count; i++)
        {
            LogicMarker m = ev.Markers[i];
            double end = m.Position + EventEdits.RegionLength(m);
            if (end > length) length = end;
        }
        return length;
    }

    private double MaxEntry(List<MultiEntry> entries)
    {
        double max = 0;
        for (int i = 0; i < entries.Count; i++) max = System.Math.Max(max, AssetLength(entries[i].Asset));
        return max;
    }

    private static double Positive(double value) => value > 0 ? value : DefaultLength;
}
