using System;
using System.Collections.Generic;
using Dirge.Project;

namespace Dirge.Runtime;

/// <summary>One parameter's target and seek-smoothed effective value.</summary>
public sealed class ParameterState
{
    public ParameterDefinition Definition { get; }
    public float Target { get; private set; }
    public float Value { get; private set; }

    public ParameterState(ParameterDefinition definition)
    {
        Definition = definition;
        Target = Value = definition.Default;
    }

    public void Set(float value, bool immediate = false)
    {
        float clamped = Math.Clamp(value, Definition.Minimum, Definition.Maximum);
        if (Definition.Kind != ParameterKind.Continuous) clamped = MathF.Round(clamped);
        Target = clamped;
        if (immediate) Value = clamped;
    }

    /// <summary>Move <see cref="Value"/> toward <see cref="Target"/> by seek speed. Returns true when the value changed.</summary>
    public bool Advance(double dt)
    {
        if (Value == Target) return false;
        float speed = Target > Value ? Definition.SeekSpeedUp : Definition.SeekSpeedDown;
        if (speed <= 0f) { Value = Target; return true; }
        float step = (float)(speed * dt);
        Value = Target > Value ? MathF.Min(Value + step, Target) : MathF.Max(Value - step, Target);
        return true;
    }
}

/// <summary>Parameters addressable by id or name. One bank for globals, one per event instance for locals.</summary>
public sealed class ParameterBank
{
    private readonly Dictionary<DirgeId, ParameterState> _byId = new();
    private readonly Dictionary<string, ParameterState> _byName = new(StringComparer.Ordinal);
    private readonly List<ParameterState> _all = new();

    public ParameterBank(IEnumerable<ParameterDefinition> definitions)
    {
        foreach (ParameterDefinition d in definitions)
        {
            var s = new ParameterState(d);
            _byId[d.Id] = s;
            _byName[d.Name] = s;
            _all.Add(s);
        }
    }

    public IReadOnlyList<ParameterState> All => _all;
    public bool TryGet(DirgeId id, out ParameterState state) => _byId.TryGetValue(id, out state!);
    public bool TryGet(string name, out ParameterState state) => _byName.TryGetValue(name, out state!);

    /// <summary>Advance every parameter; returns true if any value changed.</summary>
    public bool Advance(double dt)
    {
        bool changed = false;
        for (int i = 0; i < _all.Count; i++) changed |= _all[i].Advance(dt);
        return changed;
    }
}
