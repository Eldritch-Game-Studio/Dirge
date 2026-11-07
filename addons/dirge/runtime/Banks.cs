using System;
using System.Collections.Generic;
using Dirge.Project;
using Godot;

namespace Dirge.Driver;

/// <summary>
/// Bank preloading: a bank (<see cref="AudioBank"/>) lists events; loading it requests every audio asset those events
/// reference (clips, multi/scatterer entries, nested events) through <see cref="ResourceLoader.LoadThreadedRequest"/> and
/// hands finished streams to the <see cref="AssetCatalog"/> so the first play never blocks on disk. Polled from the
/// server's <c>_Process</c>; no threads of our own. Unloading evicts the catalog entries no other loaded bank still needs.
/// </summary>
public sealed class BankLoader
{
    private sealed class LoadedBank
    {
        public readonly List<string> Paths = new();
        public int Pending;
    }

    private readonly AssetCatalog _assets;
    private readonly Dictionary<string, LoadedBank> _banks = new(StringComparer.Ordinal);
    /// <summary>Paths with an outstanding threaded request → number of banks waiting on it.</summary>
    private readonly Dictionary<string, int> _inflight = new(StringComparer.Ordinal);
    private readonly List<string> _finished = new();
    private readonly HashSet<DirgeId> _visited = new();

    public BankLoader(AssetCatalog assets)
    {
        _assets = assets;
    }

    /// <summary>Banks requested and not yet unloaded (loading or loaded).</summary>
    public IReadOnlyCollection<string> Banks => _banks.Keys;

    /// <summary>True once every asset of a requested bank has either loaded or failed; false for unknown/unloaded banks.</summary>
    public bool IsLoaded(string name) => _banks.TryGetValue(name, out LoadedBank? bank) && bank.Pending == 0;

    /// <summary>Assets of <paramref name="name"/> still being read; -1 for unknown banks.</summary>
    public int PendingCount(string name) => _banks.TryGetValue(name, out LoadedBank? bank) ? bank.Pending : -1;

    /// <summary>Starts loading. Returns false when the project has no bank of that name. Re-requesting a bank is a no-op.</summary>
    public bool Load(AudioProject project, string name)
    {
        if (_banks.ContainsKey(name))
        {
            return true;
        }

        AudioBank? authored = null;
        for (int i = 0; i < project.Banks.Count; i++)
        {
            if (project.Banks[i].Name == name)
            {
                authored = project.Banks[i];
                break;
            }
        }

        if (authored is null)
        {
            GD.PushError($"Dirge: unknown bank '{name}'.");
            return false;
        }

        var bank = new LoadedBank();
        _visited.Clear();
        for (int i = 0; i < authored.Events.Count; i++)
        {
            CollectEvent(project, authored.Events[i], bank.Paths);
        }

        _visited.Clear();
        _banks[name] = bank;

        for (int i = 0; i < bank.Paths.Count; i++)
        {
            string path = bank.Paths[i];
            if (_assets.IsCached(path))
            {
                continue;
            }

            if (_inflight.TryGetValue(path, out int waiters))
            {
                _inflight[path] = waiters + 1;
                bank.Pending++;
                continue;
            }

            Error error = ResourceLoader.LoadThreadedRequest(path, nameof(AudioStream), useSubThreads: true);
            if (error != Error.Ok)
            {
                GD.PushError($"Dirge: bank '{name}' cannot request '{path}': {error}.");
                _assets.Register(path, null);
                continue;
            }

            _inflight[path] = 1;
            bank.Pending++;
        }

        return true;
    }

    /// <summary>Forgets the bank and evicts its assets unless another loaded bank references them. Outstanding requests are still consumed.</summary>
    public void Unload(string name)
    {
        if (!_banks.Remove(name, out LoadedBank? bank))
        {
            return;
        }

        for (int i = 0; i < bank.Paths.Count; i++)
        {
            string path = bank.Paths[i];
            if (_inflight.ContainsKey(path) || IsReferenced(path))
            {
                continue;
            }

            _assets.Evict(path);
        }
    }

    /// <summary>Moves finished threaded loads into the catalog. Call once per frame.</summary>
    public void Poll()
    {
        if (_inflight.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, int> pair in _inflight)
        {
            ResourceLoader.ThreadLoadStatus status = ResourceLoader.LoadThreadedGetStatus(pair.Key);
            if (status == ResourceLoader.ThreadLoadStatus.InProgress)
            {
                continue;
            }

            _finished.Add(pair.Key);
        }

        for (int i = 0; i < _finished.Count; i++)
        {
            string path = _finished[i];
            _inflight.Remove(path);

            AudioStream? stream = ResourceLoader.LoadThreadedGet(path) as AudioStream;
            if (stream is null)
            {
                GD.PushError($"Dirge: bank asset '{path}' failed to load.");
            }

            // Only banks still loaded want the stream; an unloaded bank's late result is dropped.
            if (IsReferenced(path))
            {
                _assets.Register(path, stream);
            }

            foreach (LoadedBank bank in _banks.Values)
            {
                if (bank.Pending > 0 && bank.Paths.Contains(path))
                {
                    bank.Pending--;
                }
            }
        }

        _finished.Clear();
    }

    private bool IsReferenced(string path)
    {
        foreach (LoadedBank bank in _banks.Values)
        {
            if (bank.Paths.Contains(path))
            {
                return true;
            }
        }

        return false;
    }

    private void CollectEvent(AudioProject project, DirgeId eventId, List<string> paths)
    {
        if (!_visited.Add(eventId))
        {
            return;
        }

        AudioEvent? audioEvent = null;
        for (int i = 0; i < project.Events.Count; i++)
        {
            if (project.Events[i].Id == eventId)
            {
                audioEvent = project.Events[i];
                break;
            }
        }

        if (audioEvent is null)
        {
            return;
        }

        for (int t = 0; t < audioEvent.Tracks.Count; t++)
        {
            List<Instrument> instruments = audioEvent.Tracks[t].Instruments;
            for (int i = 0; i < instruments.Count; i++)
            {
                CollectInstrument(project, instruments[i], paths);
            }
        }
    }

    private void CollectInstrument(AudioProject project, Instrument instrument, List<string> paths)
    {
        switch (instrument)
        {
            case ClipInstrument clip:
                AddPath(paths, clip.Asset);
                break;
            case MultiInstrument multi:
                for (int i = 0; i < multi.Entries.Count; i++)
                {
                    AddPath(paths, multi.Entries[i].Asset);
                }

                break;
            case ScattererInstrument scatterer:
                for (int i = 0; i < scatterer.Entries.Count; i++)
                {
                    AddPath(paths, scatterer.Entries[i].Asset);
                }

                break;
            case EventInstrument nested:
                CollectEvent(project, nested.Event, paths);
                break;
        }
    }

    private static void AddPath(List<string> paths, AssetRef asset)
    {
        if (!asset.IsEmpty && !paths.Contains(asset.Path))
        {
            paths.Add(asset.Path);
        }
    }
}
