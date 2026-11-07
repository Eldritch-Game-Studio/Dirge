using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;
using Godot;

namespace Dirge.Driver;

/// <summary>
/// Applies the model's <see cref="Command"/> stream to <see cref="AudioServer"/> and a pool of stream players parented under
/// <see cref="Host"/>. Owns the engine bus layout (rebuilt from <see cref="MixerGraph"/> on <see cref="LoadMixer"/>) and the
/// voice→player mapping. Steady state allocates nothing: players are pooled, bus names are pre-interned.
/// </summary>
public sealed class AudioDriver
{
    private sealed class Slot
    {
        public Node Player = null!;
        public bool Spatial;
        /// <summary>Voice currently bound; -1 when idle so a late <c>finished</c> is ignored.</summary>
        public int Voice = -1;
    }

    private readonly Node _host;
    private readonly AssetCatalog _assets;
    private readonly Stack<Slot> _free2D = new();
    private readonly Stack<Slot> _free3D = new();
    private readonly List<Slot> _all = new();
    private Slot?[] _byVoice = new Slot?[64];
    private readonly Dictionary<string, int> _busIndex = new();
    private readonly Dictionary<string, StringName> _busNames = new();
    private readonly Dictionary<string, AudioEffect[]> _effects = new();
    private readonly Dictionary<string, StringName> _parameterNames = new();
    private readonly StringName _master = new("Master");

    /// <summary>Raised when a player reports <c>finished</c> for a bound voice; the server forwards it as <see cref="VoiceFinished"/>.</summary>
    public event Action<VoiceId>? VoiceFinished;

    public Node Host => _host;
    public AssetCatalog Assets => _assets;

    public AudioDriver(Node host, AssetCatalog assets)
    {
        _host = host;
        _assets = assets;
    }

    /// <summary>Engine bus index for a model bus name; -1 when unknown.</summary>
    public int BusIndex(string bus) => _busIndex.TryGetValue(bus, out int index) ? index : -1;

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

    /// <summary>
    /// Replaces the engine bus layout with <paramref name="mixer"/>: Master stays at index 0, children are appended parent-first so
    /// every send points at a lower index, effects are instantiated from <see cref="BusEffect.TypeName"/> and configured.
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
        _effects.Clear();

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
            return;
        }

        AudioServer.SetBusName(0, master.Name);
        ConfigureBus(0, master);

        // Breadth-first from the master; a child is only placed once its parent has an index.
        var pending = new List<Bus>(mixer.Buses);
        pending.Remove(master);
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
        AudioServer.SetBusVolumeDb(index, bus.VolumeDb);
        AudioServer.SetBusMute(index, bus.Mute);
        AudioServer.SetBusSolo(index, bus.Solo);
        AudioServer.SetBusBypassEffects(index, bus.BypassEffects);

        var chain = new AudioEffect[bus.Effects.Count];
        for (int e = 0; e < bus.Effects.Count; e++)
        {
            BusEffect authored = bus.Effects[e];
            AudioEffect? effect = InstantiateEffect(authored);
            if (effect is null)
            {
                // Keep indices aligned with the authored chain so EffectSet* commands address the right slot.
                effect = new AudioEffectAmplify();
                AudioServer.AddBusEffect(index, effect, e);
                AudioServer.SetBusEffectEnabled(index, e, false);
            }
            else
            {
                AudioServer.AddBusEffect(index, effect, e);
                AudioServer.SetBusEffectEnabled(index, e, authored.Enabled);
            }

            chain[e] = effect;
        }

        _effects[bus.Name] = chain;
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

    /// <summary>Applies every command in order. The caller clears the buffer afterwards.</summary>
    public void Apply(CommandBuffer buffer)
    {
        IReadOnlyList<Command> commands = buffer.Commands;
        for (int i = 0; i < commands.Count; i++)
        {
            Apply(commands[i]);
        }
    }

    /// <summary>Stops every bound voice and returns its player to the pool; used when the host unloads or leaves the tree.</summary>
    public void StopAll()
    {
        for (int i = 0; i < _all.Count; i++)
        {
            if (_all[i].Voice >= 0)
            {
                ReleaseSlot(_all[i]);
            }
        }
    }

    public void Apply(Command command)
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

                if (slot.Spatial)
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
                Slot? slot = SlotOf(set3D.Voice);
                if (slot is { Spatial: true })
                {
                    ((AudioStreamPlayer3D)slot.Player).GlobalTransform = ToTransform(set3D.Attributes);
                }

                break;
            }
            case VoiceSetPaused paused:
            {
                Slot? slot = SlotOf(paused.Voice);
                if (slot is null)
                {
                    break;
                }

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
                if (index >= 0)
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
                if (index >= 0)
                {
                    AudioServer.SetBusBypassEffects(index, busBypass.Bypass);
                }

                break;
            }
            case EffectSetEnabled enabled:
            {
                int index = BusIndex(enabled.Bus);
                if (index >= 0 && enabled.EffectIndex >= 0 && enabled.EffectIndex < AudioServer.GetBusEffectCount(index))
                {
                    AudioServer.SetBusEffectEnabled(index, enabled.EffectIndex, enabled.Enabled);
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
        Slot? slot = SlotOf(play.Voice);
        if (slot is not null && slot.Spatial != play.Spatial)
        {
            Release(play.Voice);
            slot = null;
        }

        slot ??= Acquire(play.Voice, play.Spatial);

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
            player.UnitSize = play.MinDistance > 0 ? play.MinDistance : 1f;
            player.MaxDistance = play.MaxDistance;
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

    private Slot Acquire(VoiceId voice, bool spatial)
    {
        Stack<Slot> free = spatial ? _free3D : _free2D;
        Slot slot = free.Count > 0 ? free.Pop() : CreateSlot(spatial);
        slot.Voice = voice.Value;
        if (voice.Value >= _byVoice.Length)
        {
            Array.Resize(ref _byVoice, Math.Max(_byVoice.Length * 2, voice.Value + 1));
        }

        _byVoice[voice.Value] = slot;
        return slot;
    }

    private Slot CreateSlot(bool spatial)
    {
        var slot = new Slot { Spatial = spatial };
        if (spatial)
        {
            var player = new AudioStreamPlayer3D { Name = $"Voice3D{_all.Count}" };
            player.Finished += () => OnFinished(slot);
            slot.Player = player;
        }
        else
        {
            var player = new AudioStreamPlayer { Name = $"Voice{_all.Count}" };
            player.Finished += () => OnFinished(slot);
            slot.Player = player;
        }

        _all.Add(slot);
        _host.AddChild(slot.Player);
        return slot;
    }

    private void OnFinished(Slot slot)
    {
        if (slot.Voice < 0)
        {
            return;
        }

        var voice = new VoiceId(slot.Voice);
        ReleaseSlot(slot);
        VoiceFinished?.Invoke(voice);
    }

    private void Release(VoiceId voice)
    {
        Slot? slot = SlotOf(voice);
        if (slot is not null)
        {
            ReleaseSlot(slot);
        }
    }

    private void ReleaseSlot(Slot slot)
    {
        _byVoice[slot.Voice] = null;
        slot.Voice = -1;
        if (slot.Spatial)
        {
            var player = (AudioStreamPlayer3D)slot.Player;
            player.Stop();
            player.StreamPaused = false;
            _free3D.Push(slot);
        }
        else
        {
            var player = (AudioStreamPlayer)slot.Player;
            player.Stop();
            player.StreamPaused = false;
            _free2D.Push(slot);
        }
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
