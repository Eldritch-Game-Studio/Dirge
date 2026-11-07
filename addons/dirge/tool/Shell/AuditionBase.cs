#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dirge.Driver;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;

namespace Dirge.Tool.Shell;

/// <summary>
/// Common audition behaviour for the editor and the in-game overlay: tool-started instances, debounced rebuild after
/// structural changes, live push of value-only mixer edits while something plays, preview emitter/listener.
/// Concrete classes supply the system/driver (editor owns both; the overlay wraps the running <see cref="DirgeServer"/>).
/// </summary>
public abstract class AuditionBase : IAudition, IDisposable
{
    public const double RebuildDelaySeconds = 0.15;

    private readonly List<EventInstance> _instances = new();
    private readonly Dictionary<DirgeId, BusBuild> _built = new();
    private readonly Stopwatch _stopwatch = new();
    private double _rebuildIn = -1;
    private bool _rebuildWhenIdle;
    private Attributes3D _emitter = Attributes3D.Origin;
    private Attributes3D _listener = Attributes3D.Origin;
    private bool _disposed;

    protected AuditionBase(ProjectDocument document)
    {
        Document = document;
        Document.Changed += OnChanged;
    }

    protected ProjectDocument Document { get; }
    public DirgeSystem? System { get; protected set; }
    public event Action? Rebuilt;
    public IReadOnlyList<EventInstance> Instances => _instances;
    public double LastUpdateMs { get; protected set; }
    /// <summary>Wall time of the last rebuild (bus layout + system construction).</summary>
    public double LastRebuildMs { get; private set; }
    public bool RebuildPending => _rebuildIn >= 0;

    /// <summary>Engine driver the system's commands go through; null while unavailable.</summary>
    protected abstract AudioDriver? Driver { get; }
    /// <summary>Stops the previous system, rebuilds the engine mixer and returns a fresh system for <see cref="Document"/>.</summary>
    protected abstract DirgeSystem CreateSystem();
    protected abstract void OnListenerChanged(Attributes3D listener);
    public abstract (float Left, float Right) BusPeakDb(DirgeId bus);

    public Attributes3D PreviewEmitter
    {
        get => _emitter;
        set
        {
            _emitter = value;
            for (int i = 0; i < _instances.Count; i++)
            {
                if (_instances[i].Description.Is3D)
                {
                    _instances[i].Set3DAttributes(value);
                }
            }
        }
    }

    public Attributes3D PreviewListener
    {
        get => _listener;
        set
        {
            _listener = value;
            System?.SetListener(0, value);
            OnListenerChanged(value);
        }
    }

    public EventInstance? Play(DirgeId eventId)
    {
        if (System is null || !TryGetEvent(System, eventId, out EventDescription? description))
        {
            return null;
        }

        EventInstance instance = description!.CreateInstance();
        if (description.Is3D)
        {
            instance.Set3DAttributes(_emitter);
        }

        instance.Start();
        _instances.Add(instance);
        return instance;
    }

    public void StopAll(StopMode mode) => System?.StopAll(mode);

    /// <summary>Forces a rebuild at the next tick (debounced like structural edits).</summary>
    public void ScheduleRebuild() => _rebuildIn = RebuildDelaySeconds;

    public void Restart()
    {
        System?.StopAll(StopMode.Immediate);
        Rebuild();
    }

    /// <summary>Call once per frame from the owner's <c>_Process</c>.</summary>
    public void Tick(double dt)
    {
        if (_disposed)
        {
            return;
        }

        if (_rebuildIn >= 0)
        {
            _rebuildIn -= dt;
            if (_rebuildIn < 0)
            {
                Rebuild();
            }
        }
        else if (_rebuildWhenIdle && System is { InstanceCount: 0 })
        {
            Rebuild();
        }

        if (System is null)
        {
            return;
        }

        _stopwatch.Restart();
        Step(dt);
        LastUpdateMs = _stopwatch.Elapsed.TotalMilliseconds;
        Sweep();
    }

    /// <summary>Advance the system for one frame (the editor updates and applies here; the overlay's server already does).</summary>
    protected abstract void Step(double dt);

    protected void Rebuild()
    {
        _rebuildIn = -1;
        _rebuildWhenIdle = false;
        _stopwatch.Restart();
        _instances.Clear();
        System = null;
        try
        {
            DirgeSystem system = CreateSystem();
            system.SetListener(0, _listener);
            System = system;
            SnapshotBuild(system.Project);
        }
        catch (Exception error)
        {
            Godot.GD.PushError($"Dirge: audition rebuild failed: {error.GetBaseException().Message}");
        }

        LastRebuildMs = _stopwatch.Elapsed.TotalMilliseconds;
        Rebuilt?.Invoke();
    }

    /// <summary>Adopt an already running system (overlay attach) without rebuilding.</summary>
    protected void Adopt(DirgeSystem system)
    {
        System = system;
        SnapshotBuild(Document.Project);
        Rebuilt?.Invoke();
    }

    private void OnChanged(ChangeScope scope, DirgeId subject)
    {
        if (scope == ChangeScope.Assets || scope == ChangeScope.None)
        {
            return;
        }

        bool playing = System is { InstanceCount: > 0 };
        if (scope == ChangeScope.Mixer && playing && Driver is { } driver && System is { } system
            && MixerLivePush.TryPush(Document.Project, subject, system, driver, _built))
        {
            // Values are live; the model is re-authored from the project once nothing plays.
            _rebuildWhenIdle = true;
            return;
        }

        ScheduleRebuild();
    }

    private void SnapshotBuild(AudioProject project)
    {
        _built.Clear();
        foreach (Bus bus in project.Mixer.Buses)
        {
            _built[bus.Id] = BusBuild.Of(bus);
        }
    }

    private void Sweep()
    {
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            if (_instances[i].State == PlaybackState.Stopped)
            {
                _instances[i].Release();
                _instances.RemoveAt(i);
            }
        }
    }

    private static bool TryGetEvent(DirgeSystem system, DirgeId id, out EventDescription? description)
    {
        try
        {
            description = system.GetEvent(id);
            return true;
        }
        catch (KeyNotFoundException)
        {
            description = null;
            return false;
        }
    }

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Document.Changed -= OnChanged;
        _instances.Clear();
        System = null;
    }

    /// <summary>What the running system was built from; used to turn authored edits into runtime deltas.</summary>
    public readonly record struct BusBuild(float VolumeDb, int EffectCount, int EffectTypesHash)
    {
        public static BusBuild Of(Bus bus)
        {
            var hash = new HashCode();
            for (int i = 0; i < bus.Effects.Count; i++)
            {
                hash.Add(bus.Effects[i].TypeName);
            }

            return new BusBuild(bus.VolumeDb, bus.Effects.Count, hash.ToHashCode());
        }
    }
}

/// <summary>Pushes value-only mixer edits to a running system without rebuilding it.</summary>
public static class MixerLivePush
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="subject"/> is a bus (or one of its effects) whose structure matches the
    /// build: volume becomes a runtime override delta, mute/solo/bypass and effect values go straight to the driver.
    /// Anything else (new/removed bus or effect, VCA, routing) needs a rebuild.
    /// </summary>
    public static bool TryPush(AudioProject project, DirgeId subject, DirgeSystem system, AudioDriver driver, IReadOnlyDictionary<DirgeId, AuditionBase.BusBuild> built)
    {
        if (subject.IsNone)
        {
            return false;
        }

        Bus? bus = FindBus(project, subject);
        if (bus is null || !built.TryGetValue(bus.Id, out AuditionBase.BusBuild build) || AuditionBase.BusBuild.Of(bus) with { VolumeDb = build.VolumeDb } != build)
        {
            return false;
        }

        if (!system.Mixer.TryGetBus(bus.Name, out _))
        {
            return false;
        }

        system.Mixer.SetBusVolume(bus.Name, bus.VolumeDb - build.VolumeDb);
        driver.Apply(new BusSetMute(bus.Name, bus.Mute));
        driver.Apply(new BusSetSolo(bus.Name, bus.Solo));
        driver.Apply(new BusSetBypass(bus.Name, bus.BypassEffects));
        for (int i = 0; i < bus.Effects.Count; i++)
        {
            BusEffect effect = bus.Effects[i];
            driver.Apply(new EffectSetEnabled(bus.Name, i, effect.Enabled));
            foreach (KeyValuePair<string, float> parameter in effect.Parameters)
            {
                driver.Apply(new EffectSetParameter(bus.Name, i, parameter.Key, parameter.Value));
            }
        }

        return true;
    }

    private static Bus? FindBus(AudioProject project, DirgeId subject)
    {
        foreach (Bus bus in project.Mixer.Buses)
        {
            if (bus.Id == subject)
            {
                return bus;
            }

            for (int i = 0; i < bus.Effects.Count; i++)
            {
                if (bus.Effects[i].Id == subject)
                {
                    return bus;
                }
            }
        }

        return null;
    }
}
