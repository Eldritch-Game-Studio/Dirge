#nullable enable
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using Godot;
using NecronomiDom.Dom;

namespace Dirge.Tool.Browser;

public sealed partial class BrowserPane
{
    /// <summary>Rescans <c>res://</c> for audio files (the tree is scanned once at attach and on project load).</summary>
    public void RescanAssets()
    {
        _assetRoot = _assets.Scan();
        if (_tab == Tab.Assets) Rebuild();
    }

    private void BuildAssets()
    {
        AssetNode? root = _assetRoot ??= _assets.Scan();
        bool filtering = _query.Length > 0;
        AssetNode shown = filtering ? AssetScanner.Filter(root, _query) : root;
        if (shown.Children.Count == 0)
        {
            AddEmptyRow(filtering ? "No audio files match" : "No .wav/.ogg/.mp3 files under res://");
            return;
        }
        BuildAssetChildren(shown, 0, filtering);
    }

    private void BuildAssetChildren(AssetNode node, int depth, bool filtering)
    {
        AudioProject project = _ctx!.Project.Project;
        for (int i = 0; i < node.Children.Count; i++)
        {
            AssetNode child = node.Children[i];
            if (child.IsFolder)
            {
                bool expanded = filtering || _expandedAssetFolders.Contains(child.Path);
                HTMLElement row = AddRow(_tree, "afolder", depth, child.Name, CountFiles(child) + "", expanded ? "−" : "+");
                row.SetAttribute("data-path", child.Path);
                if (expanded) BuildAssetChildren(child, depth + 1, filtering);
            }
            else
            {
                // Visible rows resolve their length now (loads the stream once); collapsed folders stay untouched.
                double seconds = _assets.Length(child.Path);
                string meta = seconds > 0 ? Dom.Time(seconds) : "—";
                if (project.AssetLengths.ContainsKey(child.Path)) meta += " ·";
                HTMLElement row = AddRow(_tree, "asset", depth, child.Name, meta, "");
                row.SetAttribute("data-path", child.Path);
            }
        }
    }

    private static int CountFiles(AssetNode node)
    {
        int count = 0;
        for (int i = 0; i < node.Children.Count; i++) count += node.Children[i].IsFolder ? CountFiles(node.Children[i]) : 1;
        return count;
    }

    public void ToggleAssetFolder(string path)
    {
        if (!_expandedAssetFolders.Remove(path)) _expandedAssetFolders.Add(path);
        Rebuild();
    }

    /// <summary>Plays the file through a scratch player under the tool host; a second call on the same file stops it.</summary>
    public void PreviewAsset(string path)
    {
        if (_preview is null) return;
        if (_preview.Playing && _preview.Stream?.ResourcePath == path)
        {
            _preview.Stop();
            return;
        }
        var stream = ResourceLoader.Load<AudioStream>(path);
        if (stream is null) return;
        _preview.Stream = stream;
        _preview.Play();
    }

    /// <summary>Collects every asset referenced by instruments in the project.</summary>
    public static void ReferencedAssets(AudioProject project, HashSet<string> into)
    {
        for (int e = 0; e < project.Events.Count; e++)
        {
            List<Track> tracks = project.Events[e].Tracks;
            for (int t = 0; t < tracks.Count; t++)
            {
                List<Instrument> instruments = tracks[t].Instruments;
                for (int i = 0; i < instruments.Count; i++)
                {
                    switch (instruments[i])
                    {
                        case ClipInstrument clip when !clip.Asset.IsEmpty: into.Add(clip.Asset.Path); break;
                        case MultiInstrument multi: AddEntries(multi.Entries, into); break;
                        case ScattererInstrument scatterer: AddEntries(scatterer.Entries, into); break;
                    }
                }
            }
        }
    }

    private static void AddEntries(List<MultiEntry> entries, HashSet<string> into)
    {
        for (int i = 0; i < entries.Count; i++) if (!entries[i].Asset.IsEmpty) into.Add(entries[i].Asset.Path);
    }

    /// <summary>Fills <see cref="AudioProject.AssetLengths"/> for every referenced asset that loads (one undo step).</summary>
    public void ImportLengths()
    {
        if (_ctx is null) return;
        AudioProject project = _ctx.Project.Project;
        var referenced = new HashSet<string>();
        ReferencedAssets(project, referenced);
        var updates = new List<(string Path, double Length, bool HadOld, double Old)>();
        foreach (string path in referenced)
        {
            double seconds = _assets.Length(path);
            if (seconds <= 0) continue;
            bool had = project.AssetLengths.TryGetValue(path, out double old);
            if (had && old == seconds) continue;
            updates.Add((path, seconds, had, old));
        }
        if (updates.Count == 0)
        {
            Dom.SetText(_status, "Asset lengths already up to date (" + referenced.Count + " referenced)");
            return;
        }
        _ctx.Project.Mutate("Import asset lengths", ChangeScope.Assets,
            () => { for (int i = 0; i < updates.Count; i++) project.AssetLengths[updates[i].Path] = updates[i].Length; },
            () =>
            {
                for (int i = 0; i < updates.Count; i++)
                {
                    if (updates[i].HadOld) project.AssetLengths[updates[i].Path] = updates[i].Old;
                    else project.AssetLengths.Remove(updates[i].Path);
                }
            });
    }
}
