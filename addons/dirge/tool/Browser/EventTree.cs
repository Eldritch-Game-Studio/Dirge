#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;

namespace Dirge.Tool.Browser;

/// <summary>One node of the browser's event tree: a folder (path prefix) or an event leaf. Pure data, no Godot.</summary>
public sealed class EventTreeNode
{
    public EventTreeNode(string name, string fullPath, DirgeId eventId)
    {
        Name = name; FullPath = fullPath; EventId = eventId;
    }

    public string Name { get; }
    /// <summary>Slash-separated path of the folder or event ("Ambience/Wind").</summary>
    public string FullPath { get; }
    public DirgeId EventId { get; }
    public bool IsFolder => EventId.IsNone;
    public List<EventTreeNode> Children { get; } = new();
}

/// <summary>Builds a folder tree from <see cref="AudioEvent.Path"/> segments. Folders sort before events; both alphabetically.</summary>
public static class EventTree
{
    public const char Separator = '/';

    /// <param name="events">Events to place.</param>
    /// <param name="extraFolders">Folders to show even when no event lives under them (freshly created, not yet populated).</param>
    /// <param name="filter">Case-insensitive substring; folders are kept when any descendant matches. Null/empty = no filter.</param>
    /// <param name="tag">When non-empty only events carrying this exact tag are placed (and no extra folders).</param>
    public static EventTreeNode Build(IReadOnlyList<AudioEvent> events, IEnumerable<string>? extraFolders = null, string? filter = null, string? tag = null)
    {
        var root = new EventTreeNode("", "", DirgeId.None);
        bool filtering = !string.IsNullOrWhiteSpace(filter);
        string needle = filtering ? filter!.Trim() : "";
        bool tagging = !string.IsNullOrEmpty(tag);

        if (extraFolders is not null && !tagging)
        {
            foreach (string folder in extraFolders)
            {
                if (folder.Length == 0) continue;
                if (filtering && !Matches(folder, needle)) continue;
                EnsureFolder(root, folder);
            }
        }

        for (int i = 0; i < events.Count; i++)
        {
            AudioEvent ev = events[i];
            if (filtering && !Matches(ev.Path, needle)) continue;
            if (tagging && !ev.Tags.Contains(tag!)) continue;
            string path = ev.Path;
            int slash = path.LastIndexOf(Separator);
            EventTreeNode parent = slash < 0 ? root : EnsureFolder(root, path.Substring(0, slash));
            string name = slash < 0 ? path : path.Substring(slash + 1);
            parent.Children.Add(new EventTreeNode(name, path, ev.Id));
        }

        Sort(root);
        return root;
    }

    /// <summary>Distinct tags across <paramref name="events"/>, ordinal-sorted, for the browser's filter chips.</summary>
    public static List<string> AllTags(IReadOnlyList<AudioEvent> events)
    {
        var tags = new List<string>();
        for (int i = 0; i < events.Count; i++)
        {
            List<string> own = events[i].Tags;
            for (int t = 0; t < own.Count; t++) if (!tags.Contains(own[t])) tags.Add(own[t]);
        }
        tags.Sort(StringComparer.Ordinal);
        return tags;
    }

    /// <summary>A tag is a trimmed, non-empty single token (no commas; whitespace collapsed to one space).</summary>
    public static string? NormalizeTag(string raw)
    {
        string tag = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return tag.Length == 0 || tag.IndexOf(',') >= 0 ? null : tag;
    }

    /// <summary>Folder part of a path ("" for root-level events).</summary>
    public static string FolderOf(string path)
    {
        int slash = path.LastIndexOf(Separator);
        return slash < 0 ? "" : path.Substring(0, slash);
    }

    /// <summary>Last segment of a path.</summary>
    public static string NameOf(string path)
    {
        int slash = path.LastIndexOf(Separator);
        return slash < 0 ? path : path.Substring(slash + 1);
    }

    public static string Join(string folder, string name) => folder.Length == 0 ? name : folder + Separator + name;

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> itself or lies under it.</summary>
    public static bool IsUnder(string path, string folder)
        => folder.Length == 0 || path == folder || (path.StartsWith(folder, StringComparison.Ordinal) && path.Length > folder.Length && path[folder.Length] == Separator);

    /// <summary>Moves <paramref name="path"/> from <paramref name="oldFolder"/> to <paramref name="newFolder"/>, keeping the remainder.</summary>
    public static string Reparent(string path, string oldFolder, string newFolder)
    {
        string rest = oldFolder.Length == 0 ? path : path.Substring(oldFolder.Length + 1);
        return Join(newFolder, rest);
    }

    /// <summary>Returns <paramref name="desired"/> or "<paramref name="desired"/> 2", "… 3" until no event uses it.</summary>
    public static string UniquePath(IReadOnlyList<AudioEvent> events, string desired)
    {
        if (!Exists(events, desired)) return desired;
        for (int n = 2; ; n++)
        {
            string candidate = desired + " " + n;
            if (!Exists(events, candidate)) return candidate;
        }
    }

    /// <summary>A path segment may not be empty or contain the separator.</summary>
    public static bool IsValidName(string name) => name.Trim().Length > 0 && name.IndexOf(Separator) < 0;

    private static bool Exists(IReadOnlyList<AudioEvent> events, string path)
    {
        for (int i = 0; i < events.Count; i++) if (events[i].Path == path) return true;
        return false;
    }

    private static bool Matches(string path, string needle) => path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static EventTreeNode EnsureFolder(EventTreeNode root, string folderPath)
    {
        EventTreeNode current = root;
        int start = 0;
        while (start <= folderPath.Length)
        {
            int end = folderPath.IndexOf(Separator, start);
            if (end < 0) end = folderPath.Length;
            string segment = folderPath.Substring(start, end - start);
            string full = start == 0 ? segment : folderPath.Substring(0, end);
            EventTreeNode? child = null;
            for (int i = 0; i < current.Children.Count; i++)
            {
                EventTreeNode c = current.Children[i];
                if (c.IsFolder && c.Name == segment) { child = c; break; }
            }
            if (child is null)
            {
                child = new EventTreeNode(segment, full, DirgeId.None);
                current.Children.Add(child);
            }
            current = child;
            start = end + 1;
        }
        return current;
    }

    private static void Sort(EventTreeNode node)
    {
        node.Children.Sort(Compare);
        for (int i = 0; i < node.Children.Count; i++) Sort(node.Children[i]);
    }

    private static int Compare(EventTreeNode a, EventTreeNode b)
    {
        if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }
}
