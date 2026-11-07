using System;
using Dirge.Evaluation;
using Dirge.Project;

namespace Dirge.Runtime;

/// <summary>
/// A running snapshot. Effective intensity = <see cref="Intensity"/> × AHDSR envelope (<see cref="Project.Snapshot.Intensity"/>).
/// Instances are pooled by <see cref="MixerState"/>; after the release stage finishes the instance is removed and recycled,
/// so do not keep using a handle once <see cref="IsActive"/> is false.
/// </summary>
public sealed class SnapshotInstance
{
    /// <summary>Snapshot values with their targets resolved to mixer property slots (-1 = unresolved, skipped).</summary>
    internal readonly struct ResolvedValue
    {
        public readonly int Property;
        public readonly float Value;
        public ResolvedValue(int property, float value) { Property = property; Value = value; }
    }

    /// <summary>Per-snapshot immutable runtime data shared by all its instances.</summary>
    internal sealed class Definition
    {
        public readonly Snapshot Snapshot;
        public readonly ResolvedValue[] Values;
        public Definition(Snapshot snapshot, ResolvedValue[] values) { Snapshot = snapshot; Values = values; }
    }

    internal Definition Def = null!;
    internal AhdsrState Envelope;
    private MixerState? _mixer;
    private float _intensity = 1f;

    internal SnapshotInstance() { }

    public Snapshot Snapshot => Def.Snapshot;

    /// <summary>0..1 scale multiplied with the AHDSR envelope.</summary>
    public float Intensity
    {
        get => _intensity;
        set
        {
            _mixer?.System?.Recorder?.Call(this, "SetIntensity", null, null, value, false, 0);
            _intensity = Math.Clamp(value, 0f, 1f);
        }
    }

    public bool IsActive { get; private set; }

    /// <summary>Current effective intensity (scalar × envelope).</summary>
    public float EffectiveIntensity => IsActive ? _intensity * Envelope.Current : 0f;

    /// <summary>Begin the AHDSR release; the instance is removed by the mixer once the release has finished.</summary>
    public void Stop()
    {
        _mixer?.System?.Recorder?.Call(this, "StopSnapshot", null, null, 0, false, 0);
        if (!IsActive) return;
        Envelope.Release();
    }

    internal void Activate(Definition definition, MixerState mixer)
    {
        Def = definition;
        _mixer = mixer;
        Envelope = new AhdsrState(definition.Snapshot.Intensity);
        _intensity = 1f;
        IsActive = true;
    }

    internal void Deactivate() => IsActive = false;
}
