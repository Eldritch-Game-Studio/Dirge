using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;
using Godot;

namespace Dirge.Driver;

/// <summary>
/// Applies the model's <see cref="Command"/> stream to one <see cref="AudioServer"/> mixer and pooled stream players.
/// Default voices belong to <see cref="Host"/>; explicit spatial regions belong to registered World3D hosts.
/// Construct and use on the scene-tree thread. Steady state reuses players and pre-interned bus names.
/// </summary>
/// <remarks>
/// <para><b>Sync groups.</b> <see cref="VoicePlay"/> commands that share a non-zero <see cref="VoicePlay.SyncGroup"/> inside one
/// <see cref="Apply(CommandBuffer)"/> batch (the model emits an instrument group's layers in the same tick) are played by a
/// single player whose stream is an <see cref="AudioStreamSynchronized"/>, so the layers start and stay sample-locked. Each
/// voice maps to one sub-stream: <see cref="VoiceSetVolume"/> → <see cref="AudioStreamSynchronized.SetSyncStreamVolume"/>,
/// <see cref="VoiceStop"/> → that sub-stream is silenced (−100 dB) and the player stops once every member is gone. Group-wide
/// by construction: pitch, seek, pause and 3D attributes follow the <i>last</i> member command (the engine has one playback
/// position per player); the start offset is the leading member's. Members whose asset fails to load report
/// <see cref="VoiceFinished"/> immediately like plain voices. At most <see cref="AudioStreamSynchronized.MaxStreams"/> layers per
/// group; extra layers fall back to independent players. Grouping is per batch and per group id — give each event its own
/// id so simultaneous starts of different events never merge.</para>
/// <para><c>AudioStreamInteractive</c> is deliberately not used: transitions, quantization and loop logic live in the model's
/// clock so the emitted command stream stays the single oracle.</para>
/// <para><b>Pre/post-fader.</b> The engine applies bus volume after the whole chain. A bus whose <see cref="Bus.FaderIndex"/> lies
/// inside its chain gets an <see cref="AudioEffectAmplify"/> "fader" inserted at that engine slot; the real bus volume stays at
/// 0 dB and <see cref="BusSetVolume"/> drives the amplify instead. Authored effect indices at or after the fader map to engine
/// slot +1 (<see cref="EngineEffectIndex"/>); <see cref="EffectsOf"/> and <see cref="EffectSetParameter"/> keep authored indices.
/// While the bus bypasses effects the amplify is bypassed too, so the volume is mirrored onto the bus for that time.</para>
/// </remarks>
public sealed class AudioDriver
{
    private const float SilentDb = -100f;
    private const int MaxSyncStreams = (int)AudioStreamSynchronized.MaxStreams;

    private sealed class Slot
    {
        public Node Player = null!;
        public bool Spatial;
        public SpatialRegionId Region;
        // Commanded pause survives native stop/reparent/start and pending physics registration.
        public bool Paused;
        /// <summary>Voice currently bound (the leading member for a sync slot); -1 when idle so a late <c>finished</c> is ignored.</summary>
        public int Voice = -1;
        /// <summary>Non-null for a synchronized slot; the player's stream.</summary>
        public AudioStreamSynchronized? Sync;
        /// <summary>Voice id per sub-stream index; -1 once released.</summary>
        public int[]? Members;
        public int MemberCount;
        public int LiveMembers;
    }

    private sealed class RegionRoute
    {
        public SpatialRegionId Id;
        public Node3D Host = null!;
        public AudioListener3D Listener = null!;
        public World3D World = null!;
        public Action Exiting = null!;
    }

    /// <summary>In-chain fader of a bus with <see cref="Bus.FaderIndex"/> &lt; effect count.</summary>
    private sealed class Fader
    {
        public AudioEffectAmplify Effect = null!;
        /// <summary>Authored index the fader occupies; authored effects at or after it sit one engine slot later.</summary>
        public int Index;
        /// <summary>Bus currently bypasses effects (fader inactive; volume mirrored onto the bus).</summary>
        public bool Bypassed;
    }

    /// <summary>When set, sync-group plays and unusual driver decisions are printed (<c>--dirge-verbose</c>).</summary>
    public static bool Verbose { get; set; }

    private readonly Node _host;
    private readonly AssetCatalog _assets;
    private readonly Stack<Slot> _free2D = new();
    private readonly Stack<Slot> _free3D = new();
    private readonly Stack<Slot> _freeSync2D = new();
    private readonly Stack<Slot> _freeSync3D = new();
    private readonly List<Slot> _all = new();
    private Slot?[] _byVoice = new Slot?[64];
    private int[] _syncIndex = new int[64];
    private bool[] _consumed = new bool[256];
    private readonly List<VoicePlay> _group = new(MaxSyncStreams);
    private readonly Dictionary<string, int> _busIndex = new();
    private readonly Dictionary<string, StringName> _busNames = new();
    private readonly List<string> _busOrder = new();
    private readonly Dictionary<string, AudioEffect[]> _effects = new();
    private readonly Dictionary<string, Fader> _faders = new();
    private readonly Dictionary<string, StringName> _parameterNames = new();
    private readonly StringName _master = new("Master");
    private LoudnessCapture? _capture;
    private readonly int _thread = System.Environment.CurrentManagedThreadId;
    private readonly Dictionary<SpatialRegionId, RegionRoute> _regions = new();
    private readonly List<SpatialRegionId> _withdrawn = new();

    /// <summary>Raised when a player reports <c>finished</c> for a bound voice; the server forwards it as <see cref="VoiceFinished"/>.</summary>
    public event Action<VoiceId>? VoiceFinished;

    /// <summary>Raised when a sync group starts: (group id, layers on the shared player).</summary>
    public event Action<int, int>? SyncGroupStarted;

    public Node Host => _host;
    public AssetCatalog Assets => _assets;

    /// <summary>Model bus names in engine index order (Master first); valid after <see cref="LoadMixer"/>.</summary>
    public IReadOnlyList<string> BusNames => _busOrder;

    /// <summary>Loudness capture kept appended to Master across mixer rebuilds; null = no metering.</summary>
    public LoudnessCapture? Capture
    {
        get => _capture;
        set
        {
            if (ReferenceEquals(_capture, value))
            {
                return;
            }

            _capture?.Uninstall();
            _capture = value;
            _capture?.Install();
        }
    }

    public AudioDriver(Node host, AssetCatalog assets)
    {
        if (OS.GetThreadCallerId() != OS.GetMainThreadId())
            throw new InvalidOperationException("Construct AudioDriver on the scene-tree/main thread.");
        _host = host;
        _assets = assets;
        Array.Fill(_syncIndex, -1);
    }

    /// <summary>
    /// Registers a nonzero region in a distinct live World3D. Host and current listener must share an audio-enabled
    /// viewport with a current camera. Does not change the mixer or create listeners. Call on the scene-tree thread.
    /// Unregister before replacing a world; tree exit also withdraws the route synchronously.
    /// </summary>
    public void RegisterSpatialRegion(SpatialRegionId region, Node3D host, AudioListener3D listener)
    {
        CheckThread();
        if (region == default) throw new ArgumentException("Region zero is reserved for the legacy host.", nameof(region));
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(listener);
        if (_regions.ContainsKey(region)) throw new InvalidOperationException("Unregister a region before replacing it.");
        if (!GodotObject.IsInstanceValid(host) || !host.IsInsideTree() ||
            !GodotObject.IsInstanceValid(listener) || !listener.IsInsideTree())
            throw new ArgumentException("Region host and listener must be live in the scene tree.");
        var route = new RegionRoute { Id = region, Host = host, Listener = listener, World = host.GetWorld3D() };
        if (!IsLive(route)) throw new ArgumentException("Region requires a current listener and camera in the same audio-enabled viewport/world.");
        if (route.World == _host.GetViewport().FindWorld3D())
            throw new ArgumentException("An explicit region must not share the default host's World3D.");
        foreach (RegionRoute other in _regions.Values)
            if (other.World == route.World) throw new ArgumentException("Each region must own a distinct World3D.");
        route.Exiting = () => WithdrawRegion(region, retirePlayers: true);
        _regions.Add(region, route);
        host.TreeExiting += route.Exiting;
        listener.TreeExiting += route.Exiting;
    }

    /// <summary>Stops/reports all voices in a region and returns their players to the default pool. Zero is not removable.</summary>
    public bool UnregisterSpatialRegion(SpatialRegionId region) => WithdrawRegion(region, retirePlayers: false);

    private bool WithdrawRegion(SpatialRegionId region, bool retirePlayers)
    {
        CheckThread();
        if (!_regions.Remove(region, out RegionRoute? route)) return false;
        if (GodotObject.IsInstanceValid(route.Host)) route.Host.TreeExiting -= route.Exiting;
        if (GodotObject.IsInstanceValid(route.Listener)) route.Listener.TreeExiting -= route.Exiting;
        for (int i = _all.Count - 1; i >= 0; i--)
            if (_all[i].Voice >= 0 && _all[i].Spatial && _all[i].Region == region) OnFinished(_all[i], retirePlayers);
        return true;
    }

    private void CheckThread()
    {
        if (System.Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("AudioDriver node lifecycle must run on its scene-tree thread.");
    }

    private static bool IsLive(RegionRoute route)
    {
        if (!GodotObject.IsInstanceValid(route.Host) || !GodotObject.IsInstanceValid(route.Listener) ||
            !route.Host.IsInsideTree() || !route.Listener.IsInsideTree() ||
            route.Host.IsQueuedForDeletion() || route.Listener.IsQueuedForDeletion()) return false;
        Viewport viewport = route.Host.GetViewport();
        Camera3D? camera = viewport.GetCamera3D();
        return viewport == route.Listener.GetViewport() && viewport.AudioListenerEnable3D &&
            camera is not null && camera.IsInsideTree() && !camera.IsQueuedForDeletion() &&
            camera.GetWorld3D() == route.World && route.Listener.IsCurrent() &&
            route.Host.GetWorld3D() == route.World && route.Listener.GetWorld3D() == route.World;
    }

    // Once per command batch, including empty batches. No per-voice route scans or temporary collections.
    private void WithdrawInvalidRegions()
    {
        _withdrawn.Clear();
        foreach (RegionRoute route in _regions.Values)
            if (!IsLive(route)) _withdrawn.Add(route.Id);
        for (int i = 0; i < _withdrawn.Count; i++) UnregisterSpatialRegion(_withdrawn[i]);
    }

    private bool TryRegionHost(SpatialRegionId region, out Node host)
    {
        host = _host;
        if (region == default) return true;
        if (!_regions.TryGetValue(region, out RegionRoute? route)) return false;
        host = route.Host;
        return true;
    }

    private static void MovePlayer(Slot slot, Node host)
    {
        if (slot.Player.GetParent() != host) slot.Player.Reparent(host, keepGlobalTransform: false);
    }

    /// <summary>Engine bus index for a model bus name; -1 when unknown.</summary>
    public int BusIndex(string bus) => _busIndex.TryGetValue(bus, out int index) ? index : -1;

    /// <summary>Authored effect chain of a bus as instantiated; empty for unknown buses.</summary>
    public IReadOnlyList<AudioEffect> EffectsOf(string bus) => _effects.TryGetValue(bus, out AudioEffect[]? chain) ? chain : Array.Empty<AudioEffect>();

    /// <summary>The in-chain fader amplify of a bus, or null when its volume is the engine bus volume (default).</summary>
    public AudioEffectAmplify? FaderOf(string bus) => _faders.TryGetValue(bus, out Fader? fader) ? fader.Effect : null;

    /// <summary>Engine effect slot of an authored effect index (shifted by one past an in-chain fader).</summary>
    public int EngineEffectIndex(string bus, int authoredIndex)
        => _faders.TryGetValue(bus, out Fader? fader) && authoredIndex >= fader.Index ? authoredIndex + 1 : authoredIndex;

    public bool IsVoicePlaying(VoiceId voice)
    {
        Slot? slot = SlotOf(voice);
        if (slot is null)
        {
            return false;
        }

        return slot.Spatial ? ((AudioStreamPlayer3D)slot.Player).Playing : ((AudioStreamPlayer)slot.Player).Playing;
    }

    public Node? PlayerOf(VoiceId voice) => SlotOf(voice)?.Player;

    /// <summary>True when the voice plays as a layer of a synchronized group.</summary>
    public bool IsSynchronized(VoiceId voice) => SlotOf(voice)?.Sync is not null;

    /// <summary>
    /// Replaces the engine bus layout with <paramref name="mixer"/>: Master stays at index 0, children are appended parent-first so
    /// every send points at a lower index, effects are instantiated from <see cref="BusEffect.TypeName"/> and configured.
    /// The loudness capture (if any) is re-appended after Master's authored chain.
    /// </summary>
    public void LoadMixer(MixerGraph mixer)
    {
        for (int i = AudioServer.GetBusCount() - 1; i >= 1; i--)
        {
            AudioServer.RemoveBus(i);
        }

        for (int i = AudioServer.GetBusEffectCount(0) - 1; i >= 0; i--)
        {
            AudioServer.RemoveBusEffect(0, i);
        }

        _busIndex.Clear();
        _busNames.Clear();
        _busOrder.Clear();
        _effects.Clear();
        _faders.Clear();

        try
        {
            Bus? master = null;
            foreach (Bus bus in mixer.Buses)
            {
                if (bus.Parent.IsNone)
                {
                    master = bus;
                    break;
                }
            }

            if (master is null)
            {
                GD.PushError("Dirge: mixer graph has no master bus; keeping the engine's Master only.");
                AudioServer.SetBusName(0, "Master");
                _busIndex["Master"] = 0;
                _busNames["Master"] = _master;
                _busOrder.Add("Master");
                return;
            }

            AudioServer.SetBusName(0, master.Name);
            ConfigureBus(0, master);

            // Breadth-first from the master; a child is only placed once its parent has an index. Within that rule, buses
            // holding a DirgeAudioEffectReturn go first and buses holding a DirgeAudioEffectSend last: the engine mixes
            // buses from the highest index down to 0, so a return must sit at a LOWER index than every bus sending into it.
            var pending = new List<Bus>(mixer.Buses.Count);
            for (int order = 0; order <= 2; order++)
            {
                foreach (Bus bus in mixer.Buses)
                {
                    if (!ReferenceEquals(bus, master) && SendOrder(bus) == order)
                    {
                        pending.Add(bus);
                    }
                }
            }

            var placed = new Dictionary<DirgeId, int> { [master.Id] = 0 };
            bool progress = true;
            while (pending.Count > 0 && progress)
            {
                progress = false;
                for (int i = 0; i < pending.Count;)
                {
                    Bus bus = pending[i];
                    if (!placed.TryGetValue(bus.Parent, out int parentIndex))
                    {
                        i++;
                        continue;
                    }

                    int index = AudioServer.GetBusCount();
                    AudioServer.AddBus(index);
                    AudioServer.SetBusName(index, bus.Name);
                    AudioServer.SetBusSend(index, AudioServer.GetBusName(parentIndex));
                    ConfigureBus(index, bus);
                    placed[bus.Id] = index;
                    pending.RemoveAt(i);
                    progress = true;
                }
            }

            foreach (Bus orphan in pending)
            {
                GD.PushError($"Dirge: bus '{orphan.Name}' references unknown parent {orphan.Parent}; not created.");
            }

            VerifySendOrder(mixer);
        }
        finally
        {
            _capture?.Install();
        }
    }

    private void ConfigureBus(int index, Bus bus)
    {
        // The engine de-duplicates names; record what it actually used so command lookups match model names.
        string engineName = AudioServer.GetBusName(index);
        if (engineName != bus.Name)
        {
            GD.PushWarning($"Dirge: bus '{bus.Name}' was renamed to '{engineName}' by the engine (duplicate name).");
        }

        _busIndex[bus.Name] = index;
        _busNames[bus.Name] = new StringName(engineName);
        _busOrder.Add(bus.Name);
        Fader? fader = null;
        if (bus.FaderIndex < bus.Effects.Count)
        {
            fader = new Fader { Effect = new AudioEffectAmplify { VolumeDb = bus.VolumeDb }, Index = bus.FaderIndex, Bypassed = bus.BypassEffects };
            _faders[bus.Name] = fader;
        }

        AudioServer.SetBusVolumeDb(index, fader is null || fader.Bypassed ? bus.VolumeDb : 0f);
        AudioServer.SetBusMute(index, bus.Mute);
        AudioServer.SetBusSolo(index, bus.Solo);
        AudioServer.SetBusBypassEffects(index, bus.BypassEffects);

        var chain = new AudioEffect[bus.Effects.Count];
        int slot = 0;
        for (int e = 0; e < bus.Effects.Count; e++)
        {
            if (fader is not null && e == fader.Index)
            {
                AudioServer.AddBusEffect(index, fader.Effect, slot++);
            }

            BusEffect authored = bus.Effects[e];
            AudioEffect? effect = InstantiateEffect(authored);
            if (effect is null)
            {
                // Keep indices aligned with the authored chain so EffectSet* commands address the right slot.
                effect = new AudioEffectAmplify();
                AudioServer.AddBusEffect(index, effect, slot);
                AudioServer.SetBusEffectEnabled(index, slot, false);
            }
            else
            {
                AudioServer.AddBusEffect(index, effect, slot);
                AudioServer.SetBusEffectEnabled(index, slot, authored.Enabled);
            }

            chain[e] = effect;
            slot++;
        }

        _effects[bus.Name] = chain;
    }

    private const string ReturnEffectType = "DirgeAudioEffectReturn";
    private const string SendEffectType = "DirgeAudioEffectSend";
    private const string SendTargetParameter = "target_bus";

    /// <summary>0 = holds a return (place first), 2 = holds a send (place last), 1 = neither.</summary>
    private static int SendOrder(Bus bus)
    {
        int order = 1;
        for (int e = 0; e < bus.Effects.Count; e++)
        {
            string type = bus.Effects[e].TypeName;
            if (type == ReturnEffectType)
            {
                return 0;
            }

            if (type == SendEffectType)
            {
                order = 2;
            }
        }

        return order;
    }

    /// <summary>Warns for every send whose target return bus did not end up at a lower engine index (it would mix one buffer late).</summary>
    private void VerifySendOrder(MixerGraph mixer)
    {
        foreach (Bus bus in mixer.Buses)
        {
            int senderIndex = BusIndex(bus.Name);
            if (senderIndex < 0)
            {
                continue;
            }

            for (int e = 0; e < bus.Effects.Count; e++)
            {
                BusEffect effect = bus.Effects[e];
                if (effect.TypeName != SendEffectType || !effect.StringParameters.TryGetValue(SendTargetParameter, out string? target))
                {
                    continue;
                }

                int targetIndex = BusIndex(target);
                if (targetIndex < 0)
                {
                    GD.PushWarning($"Dirge: send on bus '{bus.Name}' targets unknown bus '{target}'.");
                }
                else if (targetIndex >= senderIndex)
                {
                    GD.PushWarning($"Dirge: return bus '{target}' (index {targetIndex}) is mixed before sender '{bus.Name}' (index {senderIndex}); " +
                                   "the return hears the send one mix block late. Move the return bus higher in the hierarchy (closer to Master) than the sender.");
                }
            }
        }
    }

    private AudioEffect? InstantiateEffect(BusEffect authored)
    {
        if (!ClassDB.ClassExists(authored.TypeName) || !ClassDB.IsParentClass(authored.TypeName, nameof(AudioEffect)))
        {
            GD.PushError($"Dirge: '{authored.TypeName}' is not an AudioEffect class; slot disabled.");
            return null;
        }

        if (ClassDB.Instantiate(authored.TypeName).AsGodotObject() is not AudioEffect effect)
        {
            GD.PushError($"Dirge: could not instantiate '{authored.TypeName}'; slot disabled.");
            return null;
        }

        foreach (KeyValuePair<string, float> parameter in authored.Parameters)
        {
            effect.Set(ParameterName(parameter.Key), parameter.Value);
        }

        foreach (KeyValuePair<string, string> parameter in authored.StringParameters)
        {
            effect.Set(ParameterName(parameter.Key), parameter.Value);
        }

        return effect;
    }

    /// <summary>
    /// Live-update override: writes an effect property directly, bypassing the model's change detection. The model re-asserts
    /// its own value only when that value next changes (snapshot/automation), so the override holds until then.
    /// Returns false when the bus or effect index is unknown.
    /// </summary>
    public bool SetEffectParameter(string bus, int effectIndex, string name, float value)
    {
        if (!_effects.TryGetValue(bus, out AudioEffect[]? chain) || effectIndex < 0 || effectIndex >= chain.Length)
        {
            return false;
        }

        chain[effectIndex].Set(ParameterName(name), value);
        return true;
    }

    /// <summary>Applies every command in order, folding same-batch sync-group plays into shared players. The caller clears the buffer afterwards.</summary>
    public void Apply(CommandBuffer buffer)
    {
        CheckThread();
        WithdrawInvalidRegions();
        IReadOnlyList<Command> commands = buffer.Commands;
        int count = commands.Count;
        if (count == 0)
        {
            return;
        }

        if (_consumed.Length < count)
        {
            _consumed = new bool[Math.Max(count, _consumed.Length * 2)];
        }

        for (int i = 0; i < count; i++)
        {
            if (_consumed[i])
            {
                continue;
            }

            if (commands[i] is VoicePlay { SyncGroup: not 0 } play)
            {
                PlayGroup(commands, i, play);
            }
            else
            {
                ApplyCore(commands[i]);
            }
        }

        Array.Clear(_consumed, 0, count);
    }

    /// <summary>Stops every bound voice and returns its player to the pool; used when the host unloads or leaves the tree.</summary>
    public void StopAll()
    {
        CheckThread();
        for (int i = 0; i < _all.Count; i++)
        {
            if (_all[i].Voice >= 0)
            {
                ReleaseSlot(_all[i]);
            }
        }
    }

    /// <summary>
    /// Stops everything, detaches every pooled player's stream and disposes the managed <see cref="AudioStreamSynchronized"/>
    /// resources so nothing is reported as leaked at exit. The players themselves are freed with the host node. The driver
    /// must not be used afterwards.
    /// </summary>
    public void Shutdown()
    {
        StopAll();
        foreach (RegionRoute route in _regions.Values)
        {
            if (GodotObject.IsInstanceValid(route.Host)) route.Host.TreeExiting -= route.Exiting;
            if (GodotObject.IsInstanceValid(route.Listener)) route.Listener.TreeExiting -= route.Exiting;
        }
        _regions.Clear();
        for (int i = 0; i < _all.Count; i++)
        {
            Slot slot = _all[i];
            if (slot.Spatial)
            {
                ((AudioStreamPlayer3D)slot.Player).Stream = null;
            }
            else
            {
                ((AudioStreamPlayer)slot.Player).Stream = null;
            }

            if (slot.Sync is not null)
            {
                slot.Sync.Dispose();
                slot.Sync = null;
                slot.Members = null;
            }
        }

        _all.Clear();
        _free2D.Clear();
        _free3D.Clear();
        _freeSync2D.Clear();
        _freeSync3D.Clear();
        Array.Clear(_byVoice);
        Array.Fill(_syncIndex, -1);
        Capture = null;
    }

    /// <summary>Applies one command. A <see cref="VoicePlay"/> applied alone plays as an independent voice regardless of its sync group.</summary>
    public void Apply(Command command)
    {
        CheckThread();
        WithdrawInvalidRegions();
        ApplyCore(command);
    }

    private void ApplyCore(Command command)
    {
        switch (command)
        {
            case VoicePlay play:
                Play(play);
                break;
            case VoiceStop stop:
                Release(stop.Voice);
                break;
            case VoiceSeek seek:
            {
                Slot? slot = SlotOf(seek.Voice);
                if (slot is null)
                {
                    break;
                }

                if (slot.Spatial)
                {
                    ((AudioStreamPlayer3D)slot.Player).Seek((float)seek.Seconds);
                }
                else
                {
                    ((AudioStreamPlayer)slot.Player).Seek((float)seek.Seconds);
                }

                break;
            }
            case VoiceSetVolume volume:
            {
                Slot? slot = SlotOf(volume.Voice);
                if (slot is null)
                {
                    break;
                }

                if (slot.Sync is not null)
                {
                    slot.Sync.SetSyncStreamVolume(_syncIndex[volume.Voice.Value], volume.VolumeDb);
                }
                else if (slot.Spatial)
                {
                    ((AudioStreamPlayer3D)slot.Player).VolumeDb = volume.VolumeDb;
                }
                else
                {
                    ((AudioStreamPlayer)slot.Player).VolumeDb = volume.VolumeDb;
                }

                break;
            }
            case VoiceSetPitch pitch:
            {
                Slot? slot = SlotOf(pitch.Voice);
                if (slot is null)
                {
                    break;
                }

                if (slot.Spatial)
                {
                    ((AudioStreamPlayer3D)slot.Player).PitchScale = pitch.Pitch;
                }
                else
                {
                    ((AudioStreamPlayer)slot.Player).PitchScale = pitch.Pitch;
                }

                break;
            }
            case VoiceSet3D set3D:
            {
                SetSpatialAttributes(set3D.Voice, set3D.Attributes);

                break;
            }
            case VoiceSetPaused paused:
            {
                Slot? slot = SlotOf(paused.Voice);
                if (slot is null)
                {
                    break;
                }

                slot.Paused = paused.Paused;
                if (slot.Spatial)
                {
                    ((AudioStreamPlayer3D)slot.Player).StreamPaused = paused.Paused;
                }
                else
                {
                    ((AudioStreamPlayer)slot.Player).StreamPaused = paused.Paused;
                }

                break;
            }
            case BusSetVolume busVolume:
            {
                int index = BusIndex(busVolume.Bus);
                if (index < 0)
                {
                    break;
                }

                if (_faders.TryGetValue(busVolume.Bus, out Fader? fader))
                {
                    fader.Effect.VolumeDb = busVolume.VolumeDb;
                    if (fader.Bypassed)
                    {
                        AudioServer.SetBusVolumeDb(index, busVolume.VolumeDb);
                    }
                }
                else
                {
                    AudioServer.SetBusVolumeDb(index, busVolume.VolumeDb);
                }

                break;
            }
            case BusSetMute busMute:
            {
                int index = BusIndex(busMute.Bus);
                if (index >= 0)
                {
                    AudioServer.SetBusMute(index, busMute.Mute);
                }

                break;
            }
            case BusSetSolo busSolo:
            {
                int index = BusIndex(busSolo.Bus);
                if (index >= 0)
                {
                    AudioServer.SetBusSolo(index, busSolo.Solo);
                }

                break;
            }
            case BusSetBypass busBypass:
            {
                int index = BusIndex(busBypass.Bus);
                if (index < 0)
                {
                    break;
                }

                AudioServer.SetBusBypassEffects(index, busBypass.Bypass);
                if (_faders.TryGetValue(busBypass.Bus, out Fader? fader))
                {
                    // Bypass silences the amplify fader as well; carry its volume on the bus until effects are back.
                    fader.Bypassed = busBypass.Bypass;
                    AudioServer.SetBusVolumeDb(index, busBypass.Bypass ? fader.Effect.VolumeDb : 0f);
                }

                break;
            }
            case EffectSetEnabled enabled:
            {
                // Bounded by the authored chain, not the engine count: the loudness capture sits past the chain on Master.
                int index = BusIndex(enabled.Bus);
                if (index >= 0 && _effects.TryGetValue(enabled.Bus, out AudioEffect[]? chain) && enabled.EffectIndex >= 0 && enabled.EffectIndex < chain.Length)
                {
                    AudioServer.SetBusEffectEnabled(index, EngineEffectIndex(enabled.Bus, enabled.EffectIndex), enabled.Enabled);
                }

                break;
            }
            case EffectSetParameter parameter:
            {
                if (_effects.TryGetValue(parameter.Bus, out AudioEffect[]? chain) && parameter.EffectIndex >= 0 && parameter.EffectIndex < chain.Length)
                {
                    chain[parameter.EffectIndex].Set(ParameterName(parameter.Parameter), parameter.Value);
                }

                break;
            }
            default:
                GD.PushError($"Dirge: unhandled command {command.GetType().Name}.");
                break;
        }
    }

    private void Play(VoicePlay play)
    {
        Node host = _host;
        if (play.Spatial && !TryRegionHost(play.Attributes.Region, out host))
        {
            Release(play.Voice);
            VoiceFinished?.Invoke(play.Voice);
            return;
        }
        Slot? slot = SlotOf(play.Voice);
        if (slot is not null && (slot.Spatial != play.Spatial || slot.Sync is not null))
        {
            Release(play.Voice);
            slot = null;
        }

        slot ??= Acquire(play.Voice, play.Spatial);
        slot.Paused = false;
        if (play.Spatial)
        {
            ((AudioStreamPlayer3D)slot.Player).Stop();
            MovePlayer(slot, host);
            slot.Region = play.Attributes.Region;
        }

        AudioStream? stream = play.Loop ? _assets.GetLoopingStream(play.Asset) : _assets.GetStream(play.Asset);
        if (stream is null)
        {
            Release(play.Voice);
            VoiceFinished?.Invoke(play.Voice);
            return;
        }

        if (!_busNames.TryGetValue(play.Bus, out StringName? bus))
        {
            bus = _master;
        }

        if (play.Spatial)
        {
            var player = (AudioStreamPlayer3D)slot.Player;
            player.Stream = stream;
            player.Bus = bus;
            player.VolumeDb = play.VolumeDb;
            player.PitchScale = play.Pitch;
            ApplySpatialMacros(player, play.MinDistance, play.MaxDistance, play.Rolloff, play.Doppler, play.DopplerScale);
            player.GlobalTransform = ToTransform(play.Attributes);
            player.StreamPaused = false;
            player.Play((float)play.StartSeconds);
        }
        else
        {
            var player = (AudioStreamPlayer)slot.Player;
            player.Stream = stream;
            player.Bus = bus;
            player.VolumeDb = play.VolumeDb;
            player.PitchScale = play.Pitch;
            player.StreamPaused = false;
            player.Play((float)play.StartSeconds);
        }
    }

    /// <summary>Collects every not-yet-consumed play of <paramref name="lead"/>'s group from the batch and starts them on one synchronized player.</summary>
    private void PlayGroup(IReadOnlyList<Command> commands, int first, VoicePlay lead)
    {
        _group.Clear();
        _group.Add(lead);
        _consumed[first] = true;
        for (int j = first + 1; j < commands.Count && _group.Count < MaxSyncStreams; j++)
        {
            if (!_consumed[j] && commands[j] is VoicePlay other && other.SyncGroup == lead.SyncGroup &&
                other.Spatial == lead.Spatial && (!lead.Spatial || other.Attributes.Region == lead.Attributes.Region))
            {
                _group.Add(other);
                _consumed[j] = true;
            }
        }

        if (_group.Count == 1)
        {
            Play(lead);
            _group.Clear();
            return;
        }

        for (int k = 0; k < _group.Count; k++)
        {
            Release(_group[k].Voice);
        }

        Node host = _host;
        if (lead.Spatial && !TryRegionHost(lead.Attributes.Region, out host))
        {
            for (int k = 0; k < _group.Count; k++) VoiceFinished?.Invoke(_group[k].Voice);
            _group.Clear();
            return;
        }

        Slot slot = AcquireSync(lead.Spatial);
        if (lead.Spatial)
        {
            MovePlayer(slot, host);
            slot.Region = lead.Attributes.Region;
        }
        AudioStreamSynchronized sync = slot.Sync!;
        int[] members = slot.Members!;
        sync.StreamCount = _group.Count;
        slot.MemberCount = _group.Count;
        slot.LiveMembers = 0;
        slot.Voice = -1;
        slot.Paused = false;
        for (int k = 0; k < _group.Count; k++)
        {
            VoicePlay play = _group[k];
            AudioStream? stream = play.Loop ? _assets.GetLoopingStream(play.Asset) : _assets.GetStream(play.Asset);
            sync.SetSyncStream(k, stream);
            sync.SetSyncStreamVolume(k, stream is null ? SilentDb : play.VolumeDb);
            if (stream is null)
            {
                members[k] = -1;
                VoiceFinished?.Invoke(play.Voice);
                continue;
            }

            members[k] = play.Voice.Value;
            Bind(slot, play.Voice, k);
            if (slot.Voice < 0)
            {
                slot.Voice = play.Voice.Value;
            }

            slot.LiveMembers++;
        }

        if (slot.LiveMembers == 0)
        {
            ReleaseSlot(slot);
            _group.Clear();
            return;
        }

        if (!_busNames.TryGetValue(lead.Bus, out StringName? bus))
        {
            bus = _master;
        }

        if (lead.Spatial)
        {
            var player = (AudioStreamPlayer3D)slot.Player;
            player.Stream = sync;
            player.Bus = bus;
            player.VolumeDb = 0f;
            player.PitchScale = lead.Pitch;
            ApplySpatialMacros(player, lead.MinDistance, lead.MaxDistance, lead.Rolloff, lead.Doppler, lead.DopplerScale);
            player.GlobalTransform = ToTransform(lead.Attributes);
            player.StreamPaused = false;
            player.Play((float)lead.StartSeconds);
        }
        else
        {
            var player = (AudioStreamPlayer)slot.Player;
            player.Stream = sync;
            player.Bus = bus;
            player.VolumeDb = 0f;
            player.PitchScale = lead.Pitch;
            player.StreamPaused = false;
            player.Play((float)lead.StartSeconds);
        }

        if (Verbose)
        {
            GD.Print($"Dirge: sync group {lead.SyncGroup}: {slot.LiveMembers} layers on one AudioStreamSynchronized player '{slot.Player.Name}'.");
        }

        SyncGroupStarted?.Invoke(lead.SyncGroup, slot.LiveMembers);
        _group.Clear();
    }

    private Slot Acquire(VoiceId voice, bool spatial)
    {
        Stack<Slot> free = spatial ? _free3D : _free2D;
        Slot slot = free.Count > 0 ? free.Pop() : CreateSlot(spatial, sync: false);
        slot.Voice = voice.Value;
        Bind(slot, voice, -1);
        return slot;
    }

    private Slot AcquireSync(bool spatial)
    {
        Stack<Slot> free = spatial ? _freeSync3D : _freeSync2D;
        return free.Count > 0 ? free.Pop() : CreateSlot(spatial, sync: true);
    }

    private void Bind(Slot slot, VoiceId voice, int syncIndex)
    {
        if (voice.Value >= _byVoice.Length)
        {
            int size = Math.Max(_byVoice.Length * 2, voice.Value + 1);
            Array.Resize(ref _byVoice, size);
            int old = _syncIndex.Length;
            Array.Resize(ref _syncIndex, size);
            Array.Fill(_syncIndex, -1, old, size - old);
        }

        _byVoice[voice.Value] = slot;
        _syncIndex[voice.Value] = syncIndex;
    }

    private Slot CreateSlot(bool spatial, bool sync)
    {
        var slot = new Slot { Spatial = spatial };
        string prefix = sync ? "Sync" : "Voice";
        if (spatial)
        {
            var player = new AudioStreamPlayer3D { Name = $"{prefix}3D{_all.Count}" };
            player.Finished += () => OnFinished(slot);
            slot.Player = player;
        }
        else
        {
            var player = new AudioStreamPlayer { Name = $"{prefix}{_all.Count}" };
            player.Finished += () => OnFinished(slot);
            slot.Player = player;
        }

        if (sync)
        {
            slot.Sync = new AudioStreamSynchronized();
            slot.Members = new int[MaxSyncStreams];
            Array.Fill(slot.Members, -1);
        }

        _all.Add(slot);
        _host.AddChild(slot.Player);
        return slot;
    }

    private void OnFinished(Slot slot, bool retire = false)
    {
        if (slot.Voice < 0)
        {
            return;
        }

        if (slot.Sync is null)
        {
            var voice = new VoiceId(slot.Voice);
            ReleaseSlot(slot, retire);
            VoiceFinished?.Invoke(voice);
            return;
        }

        // Every surviving layer ends together with the shared playback.
        int count = slot.MemberCount;
        Span<int> members = stackalloc int[MaxSyncStreams];
        slot.Members.AsSpan(0, count).CopyTo(members);
        ReleaseSlot(slot, retire);
        for (int k = 0; k < count; k++)
        {
            if (members[k] >= 0)
            {
                VoiceFinished?.Invoke(new VoiceId(members[k]));
            }
        }
    }

    private void Release(VoiceId voice)
    {
        Slot? slot = SlotOf(voice);
        if (slot is null)
        {
            return;
        }

        if (slot.Sync is null)
        {
            ReleaseSlot(slot);
            return;
        }

        int k = _syncIndex[voice.Value];
        _byVoice[voice.Value] = null;
        _syncIndex[voice.Value] = -1;
        slot.Members![k] = -1;
        slot.Sync.SetSyncStreamVolume(k, SilentDb);
        slot.LiveMembers--;
        if (slot.LiveMembers <= 0)
        {
            ReleaseSlot(slot);
            return;
        }

        if (slot.Voice == voice.Value)
        {
            // Keep the slot bound to a surviving member so a late `finished` is still honoured.
            for (int m = 0; m < slot.MemberCount; m++)
            {
                if (slot.Members[m] >= 0)
                {
                    slot.Voice = slot.Members[m];
                    break;
                }
            }
        }
    }

    private void ReleaseSlot(Slot slot, bool retire = false)
    {
        if (slot.Sync is null)
        {
            if (slot.Voice >= 0)
            {
                _byVoice[slot.Voice] = null;
                _syncIndex[slot.Voice] = -1;
            }
        }
        else
        {
            int[] members = slot.Members!;
            for (int k = 0; k < slot.MemberCount; k++)
            {
                if (members[k] >= 0)
                {
                    _byVoice[members[k]] = null;
                    _syncIndex[members[k]] = -1;
                }
            }
        }

        slot.Voice = -1;
        slot.LiveMembers = 0;
        slot.Paused = false;
        if (slot.Spatial)
        {
            var player = (AudioStreamPlayer3D)slot.Player;
            player.Stop();
            player.StreamPaused = false;
            player.Stream = null;
            if (!retire) MovePlayer(slot, _host);
            player.Transform = Transform3D.Identity;
            slot.Region = default;
        }
        else
        {
            var player = (AudioStreamPlayer)slot.Player;
            player.Stop();
            player.StreamPaused = false;
            player.Stream = null;
        }

        if (slot.Sync is not null)
        {
            // Drop stream references so unloaded banks can free sample data; the playback is already stopped.
            for (int k = 0; k < slot.MemberCount; k++)
            {
                slot.Sync.SetSyncStream(k, null);
                slot.Members![k] = -1;
            }

            slot.MemberCount = 0;
        }
        if (retire)
        {
            // During TreeExiting an ancestor can be busy traversing children: never reparent into/out of it.
            // Discard the native slot instead; subsequent acquisition creates a clean player.
            slot.Sync?.Dispose();
            slot.Sync = null;
            _all.Remove(slot);
            slot.Player.QueueFree();
        }
        else if (slot.Sync is not null)
        {
            (slot.Spatial ? _freeSync3D : _freeSync2D).Push(slot);
        }
        else
        {
            (slot.Spatial ? _free3D : _free2D).Push(slot);
        }
    }

    private void SetSpatialAttributes(VoiceId voice, Attributes3D attributes)
    {
        Slot? slot = SlotOf(voice);
        if (slot is not { Spatial: true }) return;
        if (!TryRegionHost(attributes.Region, out Node host))
        {
            Release(voice);
            VoiceFinished?.Invoke(voice);
            return;
        }
        var player = (AudioStreamPlayer3D)slot.Player;
        if (slot.Region != attributes.Region)
        {
            float position = player.GetPlaybackPosition();
            bool paused = slot.Paused || player.StreamPaused;
            bool playing = player.Playing;
            if (slot.Sync is not null)
            {
                // A single layer moving worlds must not drag its siblings with it.
                int index = _syncIndex[voice.Value];
                AudioStream? stream = slot.Sync.GetSyncStream(index);
                float volume = slot.Sync.GetSyncStreamVolume(index);
                StringName bus = player.Bus;
                float pitch = player.PitchScale, unit = player.UnitSize, max = player.MaxDistance;
                var attenuation = player.AttenuationModel;
                var doppler = player.DopplerTracking;
                Release(voice);
                slot = Acquire(voice, spatial: true);
                player = (AudioStreamPlayer3D)slot.Player;
                player.Stream = stream;
                player.Bus = bus;
                player.VolumeDb = volume;
                player.PitchScale = pitch;
                player.UnitSize = unit;
                player.MaxDistance = max;
                player.AttenuationModel = attenuation;
                player.DopplerTracking = doppler;
            }
            player.Stop();
            MovePlayer(slot, host);
            slot.Region = attributes.Region;
            slot.Paused = paused;
            player.GlobalTransform = ToTransform(attributes);
            if (playing || paused) player.Play(position);
            player.StreamPaused = paused;
        }
        else player.GlobalTransform = ToTransform(attributes);
    }

    private Slot? SlotOf(VoiceId voice) => voice.Value >= 0 && voice.Value < _byVoice.Length ? _byVoice[voice.Value] : null;

    private StringName ParameterName(string name)
    {
        if (!_parameterNames.TryGetValue(name, out StringName? interned))
        {
            interned = new StringName(name);
            _parameterNames[name] = interned;
        }

        return interned;
    }

    /// <summary>
    /// Distance model, range and doppler of a 3D player. <paramref name="minDistance"/> → <c>unit_size</c> (≤ 0 → 1),
    /// <paramref name="maxDistance"/> → <c>max_distance</c>. Doppler is physics-step tracking when enabled with a positive
    /// scale; the engine has no doppler scale, so the magnitude is not applied.
    /// </summary>
    private static void ApplySpatialMacros(AudioStreamPlayer3D player, float minDistance, float maxDistance, Rolloff rolloff, bool doppler, float dopplerScale)
    {
        player.UnitSize = minDistance > 0 ? minDistance : 1f;
        player.MaxDistance = maxDistance;
        player.AttenuationModel = rolloff switch
        {
            Rolloff.InverseSquare => AudioStreamPlayer3D.AttenuationModelEnum.InverseSquareDistance,
            Rolloff.Logarithmic => AudioStreamPlayer3D.AttenuationModelEnum.Logarithmic,
            Rolloff.Disabled => AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
            _ => AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
        };
        player.DopplerTracking = doppler && dopplerScale > 0f ? AudioStreamPlayer3D.DopplerTrackingEnum.PhysicsStep : AudioStreamPlayer3D.DopplerTrackingEnum.Disabled;
    }

    private static Transform3D ToTransform(Attributes3D attributes)
    {
        var position = new Vector3(attributes.Position.X, attributes.Position.Y, attributes.Position.Z);
        var forward = new Vector3(attributes.Forward.X, attributes.Forward.Y, attributes.Forward.Z);
        var up = new Vector3(attributes.Up.X, attributes.Up.Y, attributes.Up.Z);
        Basis basis = forward.IsZeroApprox() || up.IsZeroApprox() || forward.Cross(up).IsZeroApprox()
            ? Basis.Identity
            : Basis.LookingAt(forward, up);
        return new Transform3D(basis, position);
    }
}
