using System;
using System.Collections.Generic;
using Dirge.Project;

namespace Dirge.Runtime;

/// <summary>
/// One playing (or virtual) sound. Pooled by <see cref="VoicePool"/>; linked into its owning <see cref="InstrumentState"/>
/// through <see cref="NextInOwner"/>. A voice keeps its <see cref="VoiceId"/> while virtual so unvirtualizing is a restart.
/// </summary>
internal sealed class Voice
{
    public VoiceId Id;
    public EventInstance Instance = null!;
    public InstrumentState Owner = null!;
    public Voice? NextInOwner;

    public AssetRef Asset;
    public string Bus = "";
    public int BusIndex;
    public bool Loop;
    public bool Spatial;
    public Attributes3D Attributes;
    /// <summary>Scatterer voices carry their own position instead of following the instance.</summary>
    public bool OwnAttributes;
    public float MinDistance;
    public float MaxDistance;
    public int Priority;

    /// <summary>Seconds into the asset (advances by dt × pitch).</summary>
    public double Position;
    /// <summary>Asset length when the catalog knows it; otherwise <see cref="LengthKnown"/> is false and the voice waits for <see cref="VoiceFinished"/>.</summary>
    public double Length;
    public bool LengthKnown;
    public double Age;

    public float RandomVolumeDb;
    public float RandomPitch = 1f;
    public float FadeIn;
    public float FadeOutDuration;
    public float FadeOutRemaining;
    public bool FadingOut;

    /// <summary>Allowed to occupy an engine player this tick (decided by <see cref="VoicePool.ApplyLimits"/>).</summary>
    public bool IsReal;
    /// <summary>Emit <see cref="VoicePlay"/> at the next emission (new or unvirtualized).</summary>
    public bool WantsPlay;
    /// <summary>A <see cref="VoicePlay"/> has been emitted since the last stop/virtualization.</summary>
    public bool HasPlayed;
    public bool Finished;
    /// <summary>Created during the current tick; not advanced until the next one.</summary>
    public bool Fresh;

    public float VolumeDb;
    public float Pitch = 1f;
    public float LastVolumeDb;
    public float LastPitch;
    public Attributes3D LastAttributes;
    public bool LastPaused;

    public void Reset()
    {
        Instance = null!;
        Owner = null!;
        NextInOwner = null;
        Asset = default;
        Bus = "";
        BusIndex = 0;
        Loop = false;
        Spatial = false;
        Attributes = default;
        OwnAttributes = false;
        MinDistance = MaxDistance = 0f;
        Priority = 0;
        Position = Length = Age = 0;
        LengthKnown = false;
        RandomVolumeDb = 0f;
        RandomPitch = 1f;
        FadeIn = FadeOutDuration = FadeOutRemaining = 0f;
        FadingOut = false;
        IsReal = WantsPlay = HasPlayed = Finished = Fresh = false;
        VolumeDb = LastVolumeDb = 0f;
        Pitch = LastPitch = 1f;
        LastAttributes = default;
        LastPaused = false;
    }

    /// <summary>dB contribution of the fade-in / fade-out ramps (0 when neither applies).</summary>
    public float FadeDb()
    {
        float linear = 1f;
        if (FadeIn > 0f && Age < FadeIn) linear *= (float)(Age / FadeIn);
        if (FadingOut) linear *= FadeOutDuration <= 0f ? 0f : Math.Clamp(FadeOutRemaining / FadeOutDuration, 0f, 1f);
        return linear >= 1f ? 0f : Evaluation.CurveMath.LinearToDb(linear);
    }
}

/// <summary>
/// Owns every <see cref="Voice"/> in the system: id allocation (FIFO reuse so a late <see cref="VoiceFinished"/> for a
/// recycled id is unlikely to hit a new voice), the active list, and the global/per-bus real-voice limits.
/// </summary>
internal sealed class VoicePool
{
    private readonly Stack<Voice> _free = new();
    private readonly Queue<int> _freeIds = new();
    private int _nextId;
    private readonly List<Voice> _active = new(64);
    private Voice?[] _byId = new Voice?[64];
    private Voice[] _sortItems = new Voice[64];
    private double[] _sortKeys = new double[64];
    private readonly int[] _busMax;
    private readonly int[] _busCounts;

    public VoicePool(MixerGraph mixer)
    {
        int n = Math.Max(1, mixer.Buses.Count);
        _busMax = new int[n];
        _busCounts = new int[n];
        for (int i = 0; i < mixer.Buses.Count; i++) _busMax[i] = Math.Max(0, mixer.Buses[i].MaxVoices);
    }

    public int ActiveCount => _active.Count;
    public int RealCount { get; private set; }

    public Voice Acquire()
    {
        Voice v = _free.Count > 0 ? _free.Pop() : new Voice();
        int id = _freeIds.Count > 0 ? _freeIds.Dequeue() : _nextId++;
        v.Id = new VoiceId(id);
        if (id >= _byId.Length) Array.Resize(ref _byId, Math.Max(id + 1, _byId.Length * 2));
        _byId[id] = v;
        _active.Add(v);
        return v;
    }

    public void Release(Voice v)
    {
        int index = _active.IndexOf(v);
        if (index >= 0) _active.RemoveAt(index);
        _byId[v.Id.Value] = null;
        _freeIds.Enqueue(v.Id.Value);
        if (v.IsReal) RealCount = Math.Max(0, RealCount - 1);
        v.Reset();
        _free.Push(v);
    }

    public Voice? Find(VoiceId id)
    {
        int i = id.Value;
        return i >= 0 && i < _byId.Length ? _byId[i] : null;
    }

    /// <summary>
    /// Decide which voices are real this tick: instances virtualized by stealing never get players; the rest are sorted by
    /// priority (lower number first, older first on ties) and admitted while the global and per-bus caps allow.
    /// Transitions emit <see cref="VoiceStop"/> (real→virtual) or arm a <see cref="VoicePlay"/> (virtual→real).
    /// </summary>
    public void ApplyLimits(int maxReal, CommandBuffer commands)
    {
        int n = 0;
        for (int i = 0; i < _active.Count; i++)
        {
            Voice v = _active[i];
            if (v.Finished) continue;
            if (v.Instance.StealVirtual) { SetReal(v, false, commands); continue; }
            if (n == _sortItems.Length)
            {
                Array.Resize(ref _sortItems, n * 2);
                Array.Resize(ref _sortKeys, n * 2);
            }
            _sortItems[n] = v;
            _sortKeys[n] = v.Priority * 1_000_000.0 - Math.Min(v.Age, 999_999.0);
            n++;
        }
        Array.Sort(_sortKeys, _sortItems, 0, n);
        Array.Clear(_busCounts, 0, _busCounts.Length);
        int real = 0;
        for (int i = 0; i < n; i++)
        {
            Voice v = _sortItems[i];
            int bus = v.BusIndex >= 0 && v.BusIndex < _busMax.Length ? v.BusIndex : 0;
            bool admit = real < maxReal && (_busMax[bus] == 0 || _busCounts[bus] < _busMax[bus]);
            if (admit) { real++; _busCounts[bus]++; }
            SetReal(v, admit, commands);
            _sortItems[i] = null!;
        }
        RealCount = real;
    }

    private static void SetReal(Voice v, bool real, CommandBuffer commands)
    {
        if (v.IsReal == real) return;
        if (real)
        {
            v.IsReal = true;
            v.WantsPlay = true;
        }
        else
        {
            if (v.HasPlayed) commands.Add(new VoiceStop(v.Id));
            v.IsReal = false;
            v.WantsPlay = false;
            v.HasPlayed = false;
            // A one-shot whose length the model does not know cannot be resumed at the right offset; drop it.
            if (!v.Loop && !v.LengthKnown) v.Finished = true;
        }
    }
}
