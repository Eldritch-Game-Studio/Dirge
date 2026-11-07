#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace Dirge.Tool.Browser;

/// <summary>A folder or audio file under <c>res://</c>.</summary>
public sealed class AssetNode
{
    public AssetNode(string name, string path, bool isFolder) { Name = name; Path = path; IsFolder = isFolder; }
    public string Name { get; }
    /// <summary><c>res://…</c> path of the file or folder (folders without trailing slash).</summary>
    public string Path { get; }
    public bool IsFolder { get; }
    public List<AssetNode> Children { get; } = new();
}

/// <summary>Finds audio files in the project and answers their lengths lazily through <see cref="AudioStream.GetLength"/>.</summary>
public sealed class AssetScanner
{
    public const string Root = "res://";
    private static readonly string[] Extensions = { ".wav", ".ogg", ".mp3" };
    private static readonly string[] SkippedFolders = { "addons", ".godot", ".git", "bin", "obj" };

    private readonly Dictionary<string, double> _lengths = new();
    private readonly List<string> _files = new();

    /// <summary>All audio file paths found by the last <see cref="Scan"/>, sorted.</summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>Walks <c>res://</c> (skipping addons, hidden and build folders) and returns the folder tree.</summary>
    public AssetNode Scan()
    {
        _files.Clear();
        var root = new AssetNode("res://", Root, true);
        Walk(Root, root);
        _files.Sort(StringComparer.OrdinalIgnoreCase);
        return root;
    }

    public static bool IsAudioFile(string path)
    {
        for (int i = 0; i < Extensions.Length; i++)
        {
            if (path.EndsWith(Extensions[i], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Cached length in seconds; loads the stream on first request. 0 when the file cannot be loaded.</summary>
    public double Length(string path)
    {
        if (_lengths.TryGetValue(path, out double cached)) return cached;
        double seconds = 0;
        if (ResourceLoader.Exists(path))
        {
            var stream = ResourceLoader.Load<AudioStream>(path);
            if (stream is not null) seconds = stream.GetLength();
        }
        _lengths[path] = seconds;
        return seconds;
    }

    /// <summary>Filter: file path contains <paramref name="query"/>; folders survive when any descendant matches. The root is always returned (possibly empty).</summary>
    public static AssetNode Filter(AssetNode root, string query)
    {
        var copy = new AssetNode(root.Name, root.Path, true);
        FilterInto(root, query, copy);
        return copy;
    }

    private static void FilterInto(AssetNode source, string query, AssetNode target)
    {
        for (int i = 0; i < source.Children.Count; i++)
        {
            AssetNode child = source.Children[i];
            if (child.IsFolder)
            {
                var folder = new AssetNode(child.Name, child.Path, true);
                FilterInto(child, query, folder);
                if (folder.Children.Count > 0) target.Children.Add(folder);
            }
            else if (child.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                target.Children.Add(child);
            }
        }
    }

    private void Walk(string directory, AssetNode node)
    {
        using DirAccess? dir = DirAccess.Open(directory);
        if (dir is null) return;
        dir.IncludeHidden = false;
        dir.IncludeNavigational = false;
        if (dir.ListDirBegin() != Error.Ok) return;
        var folders = new List<string>();
        var files = new List<string>();
        for (string name = dir.GetNext(); name.Length > 0; name = dir.GetNext())
        {
            if (dir.CurrentIsDir())
            {
                if (name.StartsWith('.') || Array.IndexOf(SkippedFolders, name) >= 0) continue;
                folders.Add(name);
            }
            else if (IsAudioFile(name))
            {
                files.Add(name);
            }
        }
        dir.ListDirEnd();
        folders.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        string prefix = directory.EndsWith('/') ? directory : directory + "/";
        for (int i = 0; i < folders.Count; i++)
        {
            var child = new AssetNode(folders[i], prefix + folders[i], true);
            Walk(child.Path, child);
            if (child.Children.Count > 0) node.Children.Add(child);
        }
        for (int i = 0; i < files.Count; i++)
        {
            string path = prefix + files[i];
            node.Children.Add(new AssetNode(files[i], path, false));
            _files.Add(path);
        }
    }
}
