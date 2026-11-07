#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using NecronomiDom.Dom;

namespace Dirge.Tool.Browser;

public sealed partial class BrowserPane
{
    private string _selectedFolder = "";

    private void BuildEvents()
    {
        AudioProject project = _ctx!.Project.Project;
        EventTreeNode root = EventTree.Build(project.Events, _emptyFolders, _query);
        if (root.Children.Count == 0)
        {
            AddEmptyRow(_query.Length > 0 ? "No events match" : "No events — right click or + Event");
            return;
        }
        BuildEventChildren(root, 0, project);
    }

    private void BuildEventChildren(EventTreeNode node, int depth, AudioProject project)
    {
        for (int i = 0; i < node.Children.Count; i++)
        {
            EventTreeNode child = node.Children[i];
            if (child.IsFolder)
            {
                bool collapsed = _query.Length == 0 && _collapsedEventFolders.Contains(child.FullPath);
                HTMLElement row = AddRow(_tree, "folder", depth, child.Name, CountEvents(child) + "", collapsed ? "+" : "−");
                row.SetAttribute("data-path", child.FullPath);
                if (!collapsed) BuildEventChildren(child, depth + 1, project);
            }
            else
            {
                AudioEvent? ev = FindEvent(project, child.EventId);
                string meta = ev is not null && ev.Macros.Is3D ? "3D" : "";
                HTMLElement row = AddRow(_tree, "event", depth, child.Name, meta, "");
                row.SetAttribute("data-id", child.EventId.ToString());
                row.SetAttribute("data-path", child.FullPath);
            }
        }
    }

    private static int CountEvents(EventTreeNode node)
    {
        int count = 0;
        for (int i = 0; i < node.Children.Count; i++) count += node.Children[i].IsFolder ? CountEvents(node.Children[i]) : 1;
        return count;
    }

    private static AudioEvent? FindEvent(AudioProject project, DirgeId id)
    {
        for (int i = 0; i < project.Events.Count; i++) if (project.Events[i].Id == id) return project.Events[i];
        return null;
    }

    private void ToggleEventFolder(string path)
    {
        if (!_collapsedEventFolders.Remove(path)) _collapsedEventFolders.Add(path);
        Rebuild();
    }

    private void SelectFolder(string path)
    {
        _selectedFolder = path;
        _ctx!.Selection.Clear();
        foreach (Element row in _tree.QuerySelectorAll(".node.folder"))
        {
            Dom.SetClass(row, "selected", row.GetAttribute("data-path") == path);
        }
    }

    /// <summary>Folder new items go into: the selected event's folder, else the last clicked folder.</summary>
    private string SelectedFolder()
    {
        if (_ctx is null) return "";
        if (_ctx.Selection.Kind == SelectionKind.Event && FindEvent(_ctx.Project.Project, _ctx.Selection.Id) is { } ev) return EventTree.FolderOf(ev.Path);
        return _selectedFolder;
    }

    private static Bus MasterBus(AudioProject project)
    {
        for (int i = 0; i < project.Mixer.Buses.Count; i++) if (project.Mixer.Buses[i].Parent.IsNone) return project.Mixer.Buses[i];
        return project.Mixer.Buses[0];
    }

    public void NewEvent(string folder)
    {
        if (_ctx is null) return;
        AudioProject project = _ctx.Project.Project;
        string path = EventTree.UniquePath(project.Events, EventTree.Join(folder, "New Event"));
        var ev = new AudioEvent { Path = path, Bus = MasterBus(project).Id };
        ev.Tracks.Add(new Track { Name = "Track 1" });
        _emptyFolders.Remove(folder);
        _collapsedEventFolders.Remove(folder);
        _ctx.Project.Mutate("New event", ChangeScope.Events, () => project.Events.Add(ev), () => project.Events.Remove(ev), ev.Id);
        _ctx.Selection.Select(SelectionKind.Event, ev.Id);
        StartRenameOf("event", ev.Id.ToString());
    }

    /// <summary>Folders are path prefixes; a new one exists in the UI until an event is created inside it (or the project is reloaded).</summary>
    public void NewFolder(string parent)
    {
        string name = "New Folder";
        string path = EventTree.Join(parent, name);
        for (int n = 2; _emptyFolders.Contains(path) || FolderExists(path); n++) path = EventTree.Join(parent, name + " " + n);
        _emptyFolders.Add(path);
        _collapsedEventFolders.Remove(parent);
        Rebuild();
        StartRenameOf("folder", path, byPath: true);
    }

    private bool FolderExists(string path)
    {
        List<AudioEvent> events = _ctx!.Project.Project.Events;
        for (int i = 0; i < events.Count; i++) if (EventTree.IsUnder(events[i].Path, path) && events[i].Path != path) return true;
        return false;
    }

    private void StartRenameOf(string kind, string key, bool byPath = false)
    {
        string attribute = byPath ? "data-path" : "data-id";
        foreach (Element row in _tree.QuerySelectorAll(".node." + kind))
        {
            if (row.GetAttribute(attribute) == key) { BeginRename(row); return; }
        }
    }

    public void RenameEvent(DirgeId id, string newName)
    {
        if (_ctx is null || !EventTree.IsValidName(newName)) return;
        AudioProject project = _ctx.Project.Project;
        AudioEvent? ev = FindEvent(project, id);
        if (ev is null) return;
        string oldPath = ev.Path;
        string newPath = EventTree.Join(EventTree.FolderOf(oldPath), newName.Trim());
        if (newPath == oldPath) return;
        if (PathTaken(project, newPath, ev)) return;
        _ctx.Project.Mutate("Rename event", ChangeScope.Events, () => ev.Path = newPath, () => ev.Path = oldPath, id);
    }

    private static bool PathTaken(AudioProject project, string path, AudioEvent? except)
    {
        for (int i = 0; i < project.Events.Count; i++)
        {
            if (!ReferenceEquals(project.Events[i], except) && project.Events[i].Path == path) return true;
        }
        return false;
    }

    /// <summary>Renames the last segment of a folder: every event under it gets the new prefix (one undo step).</summary>
    public void RenameFolder(string folder, string newName)
    {
        if (_ctx is null || !EventTree.IsValidName(newName)) return;
        string newFolder = EventTree.Join(EventTree.FolderOf(folder), newName.Trim());
        if (newFolder == folder) return;
        if (_emptyFolders.Remove(folder)) _emptyFolders.Add(newFolder);
        if (_collapsedEventFolders.Remove(folder)) _collapsedEventFolders.Add(newFolder);
        MoveUnder(folder, newFolder, "Rename folder");
    }

    private void MoveUnder(string oldFolder, string newFolder, string label)
    {
        AudioProject project = _ctx!.Project.Project;
        var changes = new List<(AudioEvent Event, string Old, string New)>();
        for (int i = 0; i < project.Events.Count; i++)
        {
            AudioEvent ev = project.Events[i];
            if (!EventTree.IsUnder(ev.Path, oldFolder) || ev.Path == oldFolder) continue;
            changes.Add((ev, ev.Path, EventTree.Reparent(ev.Path, oldFolder, newFolder)));
        }
        if (changes.Count == 0) { Rebuild(); return; }
        _ctx.Project.Mutate(label, ChangeScope.Events,
            () => { for (int i = 0; i < changes.Count; i++) changes[i].Event.Path = changes[i].New; },
            () => { for (int i = 0; i < changes.Count; i++) changes[i].Event.Path = changes[i].Old; });
    }

    public void MoveEventToFolder(DirgeId id, string folder)
    {
        if (_ctx is null) return;
        AudioProject project = _ctx.Project.Project;
        AudioEvent? ev = FindEvent(project, id);
        if (ev is null) return;
        string oldPath = ev.Path;
        string newPath = EventTree.Join(folder, EventTree.NameOf(oldPath));
        if (newPath == oldPath) return;
        if (PathTaken(project, newPath, ev)) newPath = EventTree.UniquePath(project.Events, newPath);
        _emptyFolders.Remove(folder);
        _ctx.Project.Mutate("Move event", ChangeScope.Events, () => ev.Path = newPath, () => ev.Path = oldPath, id);
    }

    public void DeleteEvent(DirgeId id)
    {
        if (_ctx is null) return;
        AudioProject project = _ctx.Project.Project;
        AudioEvent? ev = FindEvent(project, id);
        if (ev is null) return;
        Confirm("Delete event \"" + ev.Path + "\"?", () => RemoveEvents(new List<AudioEvent> { ev }, "Delete event"));
    }

    public void DeleteFolder(string folder)
    {
        if (_ctx is null) return;
        AudioProject project = _ctx.Project.Project;
        var events = new List<AudioEvent>();
        for (int i = 0; i < project.Events.Count; i++) if (EventTree.IsUnder(project.Events[i].Path, folder)) events.Add(project.Events[i]);
        if (events.Count == 0)
        {
            _emptyFolders.Remove(folder);
            Rebuild();
            return;
        }
        Confirm("Delete folder \"" + folder + "\" and its " + events.Count + " event(s)?", () => RemoveEvents(events, "Delete folder"));
    }

    private void RemoveEvents(List<AudioEvent> events, string label)
    {
        AudioProject project = _ctx!.Project.Project;
        var indices = new int[events.Count];
        var memberships = new List<(AudioBank Bank, int Index, DirgeId Event)>();
        for (int i = 0; i < events.Count; i++)
        {
            indices[i] = project.Events.IndexOf(events[i]);
            for (int b = 0; b < project.Banks.Count; b++)
            {
                int index = project.Banks[b].Events.IndexOf(events[i].Id);
                if (index >= 0) memberships.Add((project.Banks[b], index, events[i].Id));
            }
        }
        _ctx.Project.Mutate(label, ChangeScope.Events,
            () =>
            {
                for (int i = 0; i < events.Count; i++) project.Events.Remove(events[i]);
                for (int i = 0; i < memberships.Count; i++) memberships[i].Bank.Events.Remove(memberships[i].Event);
            },
            () =>
            {
                for (int i = 0; i < events.Count; i++) project.Events.Insert(Math.Min(indices[i], project.Events.Count), events[i]);
                for (int i = 0; i < memberships.Count; i++) memberships[i].Bank.Events.Insert(Math.Min(memberships[i].Index, memberships[i].Bank.Events.Count), memberships[i].Event);
            });
        if (_ctx.Selection.Kind == SelectionKind.Event && events.Exists(e => e.Id == _ctx.Selection.Id)) _ctx.Selection.Clear();
    }

    public void DuplicateEvent(DirgeId id)
    {
        if (_ctx is null) return;
        AudioProject project = _ctx.Project.Project;
        AudioEvent? source = FindEvent(project, id);
        if (source is null) return;
        string path = EventTree.UniquePath(project.Events, source.Path + " copy");
        AudioEvent clone = EventCloner.Clone(project, source, path);
        int index = project.Events.IndexOf(source) + 1;
        _ctx.Project.Mutate("Duplicate event", ChangeScope.Events,
            () => project.Events.Insert(Math.Min(index, project.Events.Count), clone),
            () => project.Events.Remove(clone),
            clone.Id);
        _ctx.Selection.Select(SelectionKind.Event, clone.Id);
    }
}
