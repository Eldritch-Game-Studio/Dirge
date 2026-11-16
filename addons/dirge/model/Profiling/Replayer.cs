using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;

namespace Dirge.Profiling;

/// <summary>
/// Drives a fresh <see cref="DirgeSystem"/> from a <see cref="Recording"/> and compares command streams. Replay fidelity
/// rests on determinism: same project, same seed, same calls in the same order ⇒ identical commands. Calls on objects the
/// current project cannot resolve (removed events, buses, snapshots) are skipped so a recording can be replayed against an
/// edited project to see where behaviour changed.
/// </summary>
public static class Replayer
{
    /// <summary>Replay <paramref name="recording"/> on a new system built from <paramref name="project"/>; returns the recording of the replay.</summary>
    public static Recording Replay(Recording recording, AudioProject project, int? seed = null, IAssetCatalog? assets = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(project);
        var system = new DirgeSystem(project, seed ?? recording.Seed, assets, recording.Platform) { Locale = recording.Locale };
        var recorder = new Recorder(system);
        var session = new Session(system);
        List<RecordedTick> ticks = recording.Ticks;
        for (int i = 0; i < ticks.Count; i++)
        {
            RecordedTick tick = ticks[i];
            for (int k = 0; k < tick.ProgrammerSounds.Count; k++) session.QueueAnswer(tick.ProgrammerSounds[k]);
            for (int k = 0; k < tick.Calls.Count; k++) session.Apply(tick.Calls[k]);
            system.Update(tick.Dt);
            system.Commands.Clear();
        }
        return recorder.Stop();
    }

    /// <summary>First command that differs between the two recordings, or null when every tick emitted the same commands.</summary>
    public static Divergence? Compare(Recording expected, Recording actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        int ticks = Math.Min(expected.Ticks.Count, actual.Ticks.Count);
        for (int t = 0; t < ticks; t++)
        {
            List<string> e = expected.Ticks[t].Commands;
            List<string> a = actual.Ticks[t].Commands;
            int n = Math.Min(e.Count, a.Count);
            for (int i = 0; i < n; i++)
                if (!string.Equals(e[i], a[i], StringComparison.Ordinal)) return new Divergence(t, i, e[i], a[i]);
            if (e.Count != a.Count) return new Divergence(t, n, n < e.Count ? e[n] : "<no command>", n < a.Count ? a[n] : "<no command>");
        }
        if (expected.Ticks.Count != actual.Ticks.Count)
            return new Divergence(ticks, 0, expected.Ticks.Count > ticks ? "<tick>" : "<end of recording>", actual.Ticks.Count > ticks ? "<tick>" : "<end of recording>");
        return null;
    }

    /// <summary>Handle → live object mapping and programmer-sound answers for one replay.</summary>
    private sealed class Session
    {
        private readonly DirgeSystem _system;
        private readonly Dictionary<int, EventInstance> _instances = new();
        private readonly Dictionary<int, SnapshotInstance> _snapshots = new();
        private readonly Dictionary<int, Queue<string>> _answers = new();

        public Session(DirgeSystem system) { _system = system; }

        public void QueueAnswer(ProgrammerSoundRecord record)
        {
            if (!_answers.TryGetValue(record.Handle, out Queue<string>? queue)) _answers[record.Handle] = queue = new Queue<string>();
            queue.Enqueue(record.Asset);
        }

        public void Apply(ApiCall call)
        {
            switch (call.Target)
            {
                case ApiTarget.System: ApplySystem(call); break;
                case ApiTarget.Instance: ApplyInstance(call); break;
                case ApiTarget.Mixer: ApplyMixer(call); break;
                case ApiTarget.Snapshot:
                    // StartSnapshot is recorded against the created snapshot (its Result handle) but is a mixer call.
                    if (call.Method == "StartSnapshot") ApplyMixer(call); else ApplySnapshot(call);
                    break;
            }
        }

        private void ApplySystem(ApiCall call)
        {
            switch (call.Method)
            {
                case "SetGlobalParameter": _system.SetGlobalParameter(call.Name ?? "", (float)call.Value, call.Flag); break;
                case "SetListener": _system.SetListener(call.Mode, ApiCall.Unpack(call.Vector, call.Region), (float)call.Value); break;
                case "StopAll": _system.StopAll((StopMode)call.Mode); break;
                case "SetLocale": _system.Locale = call.Name ?? ""; break;
                case "SetMaxRealVoices": _system.MaxRealVoices = (int)call.Value; break;
                case "Feedback":
                    if (call.Name == "VoiceFinished") _system.Feedback(new VoiceFinished(new VoiceId((int)call.Value)));
                    else if (call.Name == "VoiceFailed") _system.Feedback(new VoiceFailed(new VoiceId((int)call.Value), call.Text ?? ""));
                    break;
            }
        }

        private void ApplyInstance(ApiCall call)
        {
            if (call.Method == "CreateInstance")
            {
                if (!_system.TryGetEvent(call.Name ?? "", out EventDescription description)) return;
                EventInstance created = description.CreateInstance();
                int handle = call.Result;
                _instances[handle] = created;
                created.Callback += (_, e) =>
                {
                    if (e is ProgrammerSoundCallback request && _answers.TryGetValue(handle, out Queue<string>? queue) && queue.Count > 0)
                        request.Asset = new AssetRef(queue.Dequeue());
                };
                return;
            }
            if (!_instances.TryGetValue(call.Handle, out EventInstance? instance)) return;
            switch (call.Method)
            {
                case "Start": instance.Start(); break;
                case "Stop": instance.Stop((StopMode)call.Mode); break;
                case "KeyOff": instance.KeyOff(); break;
                case "SetPaused": instance.SetPaused(call.Flag); break;
                case "SetParameter": instance.SetParameter(call.Name ?? "", (float)call.Value, call.Flag); break;
                case "SetParameterByLabel": instance.SetParameterByLabel(call.Name ?? "", call.Text ?? ""); break;
                case "Set3DAttributes": instance.Set3DAttributes(ApiCall.Unpack(call.Vector, call.Region)); break;
                case "SetVolume": instance.SetVolume((float)call.Value); break;
                case "SetPitch": instance.SetPitch((float)call.Value); break;
                case "SetProgrammerSound": instance.SetProgrammerSound(new AssetRef(call.Name ?? "")); break;
                case "Release": instance.Release(); break;
                case "Seek": instance.TimelinePosition = call.Value; break;
                case "SetUpdateOffset": instance.SetUpdateOffset(call.Value); break;
            }
        }

        private void ApplyMixer(ApiCall call)
        {
            MixerState mixer = _system.Mixer;
            string name = call.Name ?? "";
            switch (call.Method)
            {
                case "SetBusVolume": if (mixer.TryGetBus(name, out _)) mixer.SetBusVolume(name, (float)call.Value); break;
                case "SetBusMute": if (mixer.TryGetBus(name, out _)) mixer.SetBusMute(name, call.Flag); break;
                case "SetBusSolo": if (mixer.TryGetBus(name, out _)) mixer.SetBusSolo(name, call.Flag); break;
                case "SetVcaVolume": if (mixer.TryGetVca(name, out _)) mixer.SetVcaVolume(name, (float)call.Value); break;
                case "StartSnapshot":
                    if (!mixer.HasSnapshot(name)) return;
                    _snapshots[call.Result] = mixer.StartSnapshot(name);
                    break;
            }
        }

        private void ApplySnapshot(ApiCall call)
        {
            if (!_snapshots.TryGetValue(call.Handle, out SnapshotInstance? snapshot)) return;
            switch (call.Method)
            {
                case "SetIntensity": snapshot.Intensity = (float)call.Value; break;
                case "StopSnapshot": snapshot.Stop(); break;
            }
        }
    }
}
