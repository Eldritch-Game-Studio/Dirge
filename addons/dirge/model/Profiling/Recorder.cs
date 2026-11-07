using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;

namespace Dirge.Profiling;

/// <summary>
/// Captures a <see cref="DirgeSystem"/> session into a <see cref="Recording"/>: attach with the constructor (installs itself as
/// <see cref="DirgeSystem.Recorder"/>), drive the system normally, call <see cref="Stop"/> to detach. Only calls made from
/// outside <see cref="DirgeSystem.Update"/> and outside other API calls are recorded (the runtime brackets internal work with
/// <see cref="IApiSink.EnterInternal"/>); programmer-sound resolutions are captured separately so a replay can answer them.
/// Instances and snapshots are identified by recorder-assigned handles in creation order, which the <see cref="Replayer"/>
/// reproduces because it issues the same calls in the same order.
/// </summary>
public sealed class Recorder : IApiSink
{
    /// <summary>Handle plus whether the object was live (playing/active) at the last update, to report each end once.</summary>
    private sealed class Tracked
    {
        public int Handle;
        public bool Live;
    }

    private readonly DirgeSystem _system;
    private readonly Dictionary<object, Tracked> _handles = new(ReferenceEqualityComparer.Instance);
    private int _nextHandle;
    private int _internalDepth;
    private readonly List<object> _ended = new();
    private RecordedTick _pending = new();
    private RecordedTick? _current;

    public Recording Recording { get; } = new();
    public bool IsAttached { get; private set; }

    public Recorder(DirgeSystem system)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        if (system.Recorder is not null) throw new InvalidOperationException("The system already has a recorder attached.");
        Recording.ProjectName = system.Project.Name;
        Recording.Seed = system.Seed;
        Recording.Platform = system.Platform;
        Recording.Locale = system.Locale;
        system.Recorder = this;
        IsAttached = true;
    }

    /// <summary>Detach from the system; calls issued after this are not recorded. Returns the recording.</summary>
    public Recording Stop()
    {
        if (IsAttached)
        {
            if (ReferenceEquals(_system.Recorder, this)) _system.Recorder = null;
            IsAttached = false;
        }
        return Recording;
    }

    /// <summary>Handle the recorder assigned to <paramref name="target"/> (instance or snapshot), or -1.</summary>
    public int HandleOf(object target) => _handles.TryGetValue(target, out Tracked? t) ? t.Handle : -1;

    private bool Suppressed => _internalDepth > 0;

    private int Resolve(object? target, out ApiTarget kind)
    {
        switch (target)
        {
            case null:
                kind = ApiTarget.System;
                return -1;
            case MixerState:
                kind = ApiTarget.Mixer;
                return -1;
            case EventInstance:
                kind = ApiTarget.Instance;
                return HandleOf(target);
            case SnapshotInstance:
                kind = ApiTarget.Snapshot;
                return HandleOf(target);
            default:
                kind = ApiTarget.System;
                return -1;
        }
    }

    void IApiSink.Created(object target, string method, string name)
    {
        if (Suppressed) return;
        int handle = _nextHandle++;
        _handles[target] = new Tracked { Handle = handle, Live = target is SnapshotInstance }; // instances become live when they start
        ApiTarget kind = target is SnapshotInstance ? ApiTarget.Snapshot : ApiTarget.Instance;
        _pending.Calls.Add(new ApiCall { Method = method, Target = kind, Name = name, Result = handle });
    }

    void IApiSink.Call(object? target, string method, string? name, string? text, double value, bool flag, int mode)
    {
        if (Suppressed) return;
        int handle = Resolve(target, out ApiTarget kind);
        if (handle < 0 && (kind == ApiTarget.Instance || kind == ApiTarget.Snapshot)) return; // not handed out by the API (nested child)
        _pending.Calls.Add(new ApiCall { Method = method, Target = kind, Handle = handle, Name = name, Text = text, Value = value, Flag = flag, Mode = mode });
    }

    void IApiSink.Call3D(object? target, string method, int index, in Attributes3D attributes, float weight)
    {
        if (Suppressed) return;
        int handle = Resolve(target, out ApiTarget kind);
        if (handle < 0 && kind == ApiTarget.Instance) return;
        _pending.Calls.Add(new ApiCall { Method = method, Target = kind, Handle = handle, Mode = index, Value = weight, Vector = ApiCall.Pack(attributes) });
    }

    void IApiSink.ProgrammerSound(EventInstance instance, string key, AssetRef asset)
    {
        RecordedTick? tick = _current ?? _pending;
        tick.ProgrammerSounds.Add(new ProgrammerSoundRecord { Handle = HandleOf(instance), Key = key, Asset = asset.Path });
    }

    void IApiSink.EnterInternal() => _internalDepth++;

    void IApiSink.ExitInternal()
    {
        if (_internalDepth > 0) _internalDepth--;
    }

    void IApiSink.BeginUpdate(DirgeSystem system, double dt)
    {
        _current = _pending;
        _current.Dt = dt;
        _current.Time = system.Time;
        _pending = new RecordedTick();
    }

    void IApiSink.EndUpdate(DirgeSystem system)
    {
        RecordedTick tick = _current ?? _pending;
        IReadOnlyList<Command> commands = system.Commands.Commands;
        for (int i = 0; i < commands.Count; i++) tick.Commands.Add(CommandText.Format(commands[i]));
        tick.InstanceCount = system.InstanceCount;
        tick.RealVoiceCount = system.RealVoiceCount;
        CollectEnded(tick);
        Recording.Ticks.Add(tick);
        _current = null;
    }

    /// <summary>
    /// Report, once, every tracked instance that is now Stopped (one-shots end without an API call) and every snapshot that
    /// finished. Stopped instances keep their handle (the game may Start them again); destroyed instances and finished
    /// snapshots (recycled by the runtime) are forgotten so a reused object gets a fresh handle.
    /// </summary>
    private void CollectEnded(RecordedTick tick)
    {
        foreach (KeyValuePair<object, Tracked> entry in _handles)
        {
            Tracked t = entry.Value;
            bool live;
            bool forget;
            switch (entry.Key)
            {
                case EventInstance instance:
                    live = instance.State != PlaybackState.Stopped;
                    forget = instance.IsDestroyed;
                    break;
                case SnapshotInstance snapshot:
                    live = snapshot.IsActive;
                    forget = !live;
                    break;
                default:
                    live = false;
                    forget = true;
                    break;
            }
            if (t.Live && !live) tick.Ended.Add(t.Handle);
            t.Live = live;
            if (forget) _ended.Add(entry.Key);
        }
        for (int i = 0; i < _ended.Count; i++) _handles.Remove(_ended[i]);
        _ended.Clear();
    }
}
