#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using Godot;
using NecronomiDom.Dom;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Browser;

/// <summary>
/// Project browser: Events (folder tree by path), Assets (audio files under <c>res://</c>), Snapshots, global Parameters and
/// Banks. One NecronomiDOM document; rows are rebuilt per tab from the model and never hold model state themselves.
/// Drag sources: <see cref="EventDrag"/> and <see cref="AssetDrag"/> through Godot's Control drag forwarding on the host.
/// </summary>
public sealed partial class BrowserPane : IToolPane, IToolPaneCommands
{
    public enum Tab { Events, Assets, Snapshots, Parameters, Banks }

    private const int MaxDepthClass = 8;

    private readonly BrowserRoot _root = new() { Name = "Browser" };
    private readonly DoubleClick _doubleClick = new();
    private readonly HashSet<string> _collapsedEventFolders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedAssetFolders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emptyFolders = new(StringComparer.Ordinal);
    private readonly AssetScanner _assets = new();

    private ToolContext? _ctx;
    private DocumentHost? _host;
    private Document? _doc;
    private Element _tree = null!, _members = null!, _memberList = null!, _status = null!, _tagFilter = null!, _tagChips = null!;
    private HTMLElement _tagEditor = null!;
    private HTMLInputElement _search = null!, _tagInput = null!;
    private HTMLElement _addButton = null!, _addFolderButton = null!, _importButton = null!;
    private PopupMenu? _menu;
    private ConfirmationDialog? _confirm;
    private AudioStreamPlayer? _preview;
    private AssetNode? _assetRoot;
    private Tab _tab = Tab.Events;
    private string _query = "";
    private string _tagQuery = "";
    private DirgeId _selectedBank;
    private Element? _dropHover;
    private Action? _pendingConfirm;
    private RenameState? _rename;
    private readonly Dictionary<(string Kind, string Key), Element> _selectionRows = new();
    private Element? _selectedRow;

    public string Title => "Browser";
    public Control Root => _root;
    public Tab ActiveTab => _tab;
    public DocumentHost Host => _host ?? throw new InvalidOperationException("Browser is not attached.");

    public void Attach(ToolContext context)
    {
        _ctx = context;
        _host = ToolDocuments.Create("browser.html");
        _host.Name = "Document";
        // Unhandled buttons (right click) must reach the root for the context menu; the document still consumes what it uses.
        _host.MouseFilter = Control.MouseFilterEnum.Pass;
        _root.AddChild(_host);
        _root.ContextRequested += OnContextRequested;
        _root.DragEnded += OnDragEnded;
        _host.SetDragForwarding(
            Callable.From<Vector2, Variant>(GetDragData),
            Callable.From<Vector2, Variant, bool>(CanDropData),
            Callable.From<Vector2, Variant>(DropData));

        _menu = new PopupMenu { Name = "ContextMenu" };
        _menu.IdPressed += OnMenuItem;
        _root.AddChild(_menu);
        _confirm = new ConfirmationDialog { Name = "Confirm", Title = "Dirge" };
        _confirm.Confirmed += () => { Action? action = _pendingConfirm; _pendingConfirm = null; action?.Invoke(); };
        _confirm.Canceled += () => _pendingConfirm = null;
        _root.AddChild(_confirm);
        _preview = new AudioStreamPlayer { Name = "BrowserPreview" };
        context.Host.AddChild(_preview);

        BindDocument(_host.Document ?? throw new InvalidOperationException("ToolDocuments.Create did not load browser.html."));

        context.Project.Changed += OnProjectChanged;
        context.Selection.Changed += OnSelectionChanged;
        _assetRoot = _assets.Scan();
        ShowTab(Tab.Events);
    }

    public void Detach()
    {
        if (_ctx is null) return;
        _ctx.Project.Changed -= OnProjectChanged;
        _ctx.Selection.Changed -= OnSelectionChanged;
        _root.ContextRequested -= OnContextRequested;
        _root.DragEnded -= OnDragEnded;
        _preview?.QueueFree();
        _preview = null;
        _menu?.QueueFree();
        _menu = null;
        _confirm?.QueueFree();
        _confirm = null;
        _host?.QueueFree();
        _host = null;
        _doc = null;
        _rename = null;
        _selectionRows.Clear();
        _selectedRow = null;
        _shownTags.Clear();
        _ctx = null;
    }

    // ---- document wiring -------------------------------------------------------------------------------------------

    private void BindDocument(Document doc)
    {
        _doc = doc;
        _tree = doc.GetElementById("tree")!;
        _members = doc.GetElementById("members")!;
        _memberList = doc.GetElementById("member-list")!;
        _status = doc.GetElementById("status")!;
        _search = (HTMLInputElement)doc.GetElementById("search")!;
        _addButton = (HTMLElement)doc.GetElementById("add")!;
        _addFolderButton = (HTMLElement)doc.GetElementById("add-folder")!;
        _importButton = (HTMLElement)doc.GetElementById("import-lengths")!;
        _tagFilter = doc.GetElementById("tag-filter")!;
        _tagEditor = (HTMLElement)doc.GetElementById("tag-editor")!;
        _tagChips = doc.GetElementById("tag-chips")!;
        _tagInput = (HTMLInputElement)doc.GetElementById("tag-input")!;

        BindTab("tab-events", Tab.Events);
        BindTab("tab-assets", Tab.Assets);
        BindTab("tab-snapshots", Tab.Snapshots);
        BindTab("tab-parameters", Tab.Parameters);
        BindTab("tab-banks", Tab.Banks);

        Dom.On(_search, "input", _ => { _query = _search.Value.Trim(); Rebuild(); });
        Dom.On(_addButton, "click", _ => AddItem());
        Dom.On(_addFolderButton, "click", _ => NewFolder(SelectedFolder()));
        Dom.On(_importButton, "click", _ => ImportLengths());
        Dom.On(_tagFilter, "click", OnTagFilterClick);
        Dom.On(_tagChips, "click", OnTagChipClick);
        Dom.On(_tagInput, "keydown", OnTagInputKey);
        Dom.On(_tagInput, "change", _ => CommitTagInput());

        Dom.On(_tree, "click", OnTreeClick);
        Dom.On(_memberList, "click", OnMemberClick);
        Dom.On(_tree, "keydown", OnTreeKey);
        Dom.On(_tree, "focusout", OnTreeFocusOut);
    }

    private void BindTab(string id, Tab tab)
    {
        Element button = _doc!.GetElementById(id)!;
        Dom.On(button, "click", _ => ShowTab(tab));
    }

    public void ShowTab(Tab tab)
    {
        _tab = tab;
        CancelRename();
        foreach (Element button in _doc!.QuerySelectorAll(".tab"))
        {
            Dom.SetClass(button, "active", button.Id == TabId(tab));
        }
        ((HTMLElement)_members).Hidden = tab != Tab.Banks;
        _importButton.Hidden = tab != Tab.Assets;
        _addFolderButton.Hidden = tab != Tab.Events;
        _addButton.Hidden = tab == Tab.Assets;
        ((HTMLElement)_tagFilter).Hidden = tab != Tab.Events;
        if (tab != Tab.Events) _tagEditor.Hidden = true;
        Dom.SetText(_addButton, tab switch
        {
            Tab.Events => "+ Event",
            Tab.Snapshots => "+ Snapshot",
            Tab.Parameters => "+ Parameter",
            Tab.Banks => "+ Bank",
            _ => "+",
        });
        Rebuild();
    }

    private static string TabId(Tab tab) => tab switch
    {
        Tab.Events => "tab-events",
        Tab.Assets => "tab-assets",
        Tab.Snapshots => "tab-snapshots",
        Tab.Parameters => "tab-parameters",
        _ => "tab-banks",
    };

    /// <summary>Rebuilds the active tab's rows from the model.</summary>
    public void Rebuild()
    {
        if (_ctx is null || _doc is null) return;
        _rename = null;
        _dropHover = null;
        _selectionRows.Clear();
        _selectedRow = null;
        ClearChildren(_tree);
        switch (_tab)
        {
            case Tab.Events: BuildTagFilter(); BuildEvents(); break;
            case Tab.Assets: BuildAssets(); break;
            case Tab.Snapshots: BuildSnapshots(); break;
            case Tab.Parameters: BuildParameters(); break;
            case Tab.Banks: BuildBanks(); break;
        }
        foreach (Element row in _tree.QuerySelectorAll(".node"))
        {
            string? kind = row.GetAttribute("data-kind");
            string? key = row.GetAttribute("data-id") ?? row.GetAttribute("data-path");
            if (kind is not null && key is not null) _selectionRows[(kind, key)] = row;
        }
        UpdateStatus();
        ApplySelection();
        if (_tab == Tab.Events) BuildTagEditor();
    }

    private static void ClearChildren(Element element)
    {
        while (element.FirstChild is { } child) element.RemoveChild(child);
    }

    private void OnProjectChanged(ChangeScope scope, DirgeId subject)
    {
        if (scope == ChangeScope.All)
        {
            _emptyFolders.Clear();
            _assetRoot = _assets.Scan();
            Rebuild();
            return;
        }
        bool relevant = _tab switch
        {
            Tab.Events => (scope & ChangeScope.Events) != 0,
            Tab.Assets => (scope & ChangeScope.Assets) != 0,
            Tab.Snapshots => (scope & ChangeScope.Snapshots) != 0,
            Tab.Parameters => (scope & ChangeScope.GlobalParameters) != 0,
            Tab.Banks => (scope & ChangeScope.Events) != 0,
            _ => false,
        };
        if (relevant) Rebuild();
    }

    private void OnSelectionChanged()
    {
        ApplySelection();
        if (_tab == Tab.Events) BuildTagEditor();
    }

    private void ApplySelection()
    {
        if (_ctx is null) return;
        Selection selection = _ctx.Selection;
        (string Kind, string Key) key = _tab == Tab.Banks ? ("bank", _selectedBank.ToString()) : selection.Kind switch
        {
            SelectionKind.None => ("folder", _selectedFolder),
            SelectionKind.Event => ("event", selection.Id.ToString()),
            SelectionKind.Asset => ("asset", selection.AssetPath ?? ""),
            SelectionKind.Snapshot => ("snapshot", selection.Id.ToString()),
            SelectionKind.Parameter when selection.Owner.IsNone => ("parameter", selection.Id.ToString()),
            _ => ("", ""),
        };
        _selectionRows.TryGetValue(key, out Element? selected);
        if (ReferenceEquals(selected, _selectedRow)) return;
        if (_selectedRow is not null) Dom.SetClass(_selectedRow, "selected", false);
        _selectedRow = selected;
        if (selected is not null) Dom.SetClass(selected, "selected", true);
    }

    // ---- rows ---------------------------------------------------------------------------------------------------------

    private HTMLElement AddRow(Element parent, string kind, int depth, string label, string? meta, string? twisty)
    {
        HTMLElement row = Dom.Add(parent, "div", "node " + kind + " d" + Math.Min(depth, MaxDepthClass));
        row.SetAttribute("data-kind", kind);
        Dom.Add(row, "p", "twisty", twisty ?? "");
        Dom.Add(row, "div", "icon");
        Dom.Add(row, "p", "name", label);
        if (meta is not null) Dom.Add(row, "p", "meta", meta);
        return row;
    }

    private void AddEmptyRow(string text)
    {
        HTMLElement row = Dom.Add(_tree, "div", "node empty d0");
        Dom.Add(row, "p", "name", text);
    }

    private static Element? RowOf(NecronomiDom.Dom.Event e) => Dom.Closest(e, ".node");

    private void OnTreeClick(NecronomiDom.Dom.Event e)
    {
        if (_ctx is null) return;
        Element? row = RowOf(e);
        if (row is null || row.ClassList.Contains("empty")) return;
        if (_rename is not null && ReferenceEquals(row, _rename.Row)) return;
        CommitRename();
        string? kind = row.GetAttribute("data-kind");
        bool onTwisty = Dom.Closest(e, ".twisty") is not null;
        bool second = _doubleClick.Register(row);
        switch (kind)
        {
            case "folder":
            {
                string path = row.GetAttribute("data-path")!;
                if (onTwisty || second) { ToggleEventFolder(path); _doubleClick.Reset(); }
                else SelectFolder(path);
                break;
            }
            case "event":
            {
                DirgeId id = DirgeId.Parse(row.GetAttribute("data-id")!);
                _ctx.Selection.Select(SelectionKind.Event, id);
                if (second) _ctx.Audition.Play(id);
                break;
            }
            case "afolder":
            {
                string path = row.GetAttribute("data-path")!;
                if (onTwisty || second) { ToggleAssetFolder(path); _doubleClick.Reset(); }
                break;
            }
            case "asset":
            {
                string path = row.GetAttribute("data-path")!;
                _ctx.Selection.SelectAsset(path);
                if (second) PreviewAsset(path);
                break;
            }
            case "snapshot":
            {
                DirgeId id = DirgeId.Parse(row.GetAttribute("data-id")!);
                _ctx.Selection.Select(SelectionKind.Snapshot, id);
                if (second) BeginRename(row);
                break;
            }
            case "parameter":
            {
                DirgeId id = DirgeId.Parse(row.GetAttribute("data-id")!);
                _ctx.Selection.Select(SelectionKind.Parameter, id);
                if (second) BeginRename(row);
                break;
            }
            case "bank":
            {
                DirgeId id = DirgeId.Parse(row.GetAttribute("data-id")!);
                _selectedBank = id;
                _ctx.Selection.Select(SelectionKind.Bank, id);
                BuildBankMembers();
                ApplySelection();
                if (second) BeginRename(row);
                break;
            }
        }
    }

    private void UpdateStatus()
    {
        if (_ctx is null) return;
        AudioProject project = _ctx.Project.Project;
        string text = _tab switch
        {
            Tab.Events => _tagQuery.Length > 0 ? CountTagged(project, _tagQuery) + " of " + project.Events.Count + " events tagged '" + _tagQuery + "'" : project.Events.Count + " events",
            Tab.Assets => (_assets.Files.Count) + " audio files · " + project.AssetLengths.Count + " lengths imported",
            Tab.Snapshots => project.Snapshots.Count + " snapshots",
            Tab.Parameters => project.GlobalParameters.Count + " global parameters",
            Tab.Banks => project.Banks.Count + " banks",
            _ => "",
        };
        Dom.SetText(_status, text);
    }

    // ---- inline rename ----------------------------------------------------------------------------------------------

    private sealed class RenameState
    {
        public RenameState(Element row, HTMLInputElement input, Element label, string original) { Row = row; Input = input; Label = label; Original = original; }
        public Element Row { get; }
        public HTMLInputElement Input { get; }
        public Element Label { get; }
        public string Original { get; }
    }

    /// <summary>Swaps the row label for a text input; Enter commits, Escape cancels, focus loss commits.</summary>
    public void BeginRename(Element row)
    {
        if (_doc is null) return;
        CommitRename();
        Element? label = row.QuerySelector(".name");
        if (label is null) return;
        string original = label.TextContent ?? "";
        var input = (HTMLInputElement)_doc.CreateElement("input");
        input.Type = "text";
        input.ClassName = "rename";
        input.Value = original;
        row.ReplaceChild(input, label);
        _rename = new RenameState(row, input, label, original);
        input.Focus();
        _host?.FlushPendingLayout();
    }

    private void OnTreeKey(NecronomiDom.Dom.Event e)
    {
        if (_rename is null || e is not KeyboardEvent key || !ReferenceEquals(e.Target, _rename.Input)) return;
        if (key.Key == "Enter") { key.PreventDefault(); CommitRename(); }
        else if (key.Key == "Escape") { key.PreventDefault(); key.StopPropagation(); CancelRename(); }
    }

    private void OnTreeFocusOut(NecronomiDom.Dom.Event e)
    {
        if (_rename is not null && ReferenceEquals(e.Target, _rename.Input)) CommitRename();
    }

    public void CancelRename()
    {
        RenameState? rename = _rename;
        _rename = null;
        if (rename is null || !rename.Input.IsConnected) return;
        rename.Row.ReplaceChild(rename.Label, rename.Input);
    }

    public void CommitRename()
    {
        RenameState? rename = _rename;
        if (rename is null || _ctx is null) return;
        _rename = null;
        string value = rename.Input.Value.Trim();
        if (rename.Input.IsConnected) rename.Row.ReplaceChild(rename.Label, rename.Input);
        if (value.Length == 0 || value == rename.Original) return;
        string kind = rename.Row.GetAttribute("data-kind") ?? "";
        switch (kind)
        {
            case "event": RenameEvent(DirgeId.Parse(rename.Row.GetAttribute("data-id")!), value); break;
            case "folder": RenameFolder(rename.Row.GetAttribute("data-path")!, value); break;
            case "snapshot": RenameSnapshot(DirgeId.Parse(rename.Row.GetAttribute("data-id")!), value); break;
            case "parameter": RenameParameter(DirgeId.Parse(rename.Row.GetAttribute("data-id")!), value); break;
            case "bank": RenameBank(DirgeId.Parse(rename.Row.GetAttribute("data-id")!), value); break;
        }
    }

    // ---- context menu -------------------------------------------------------------------------------------------------

    private enum MenuItem { Play, Rename, Duplicate, Delete, NewEvent, NewFolder, Preview, ImportLength, Add }

    private Element? _menuRow;

    private void OnContextRequested(Vector2 rootPosition)
    {
        if (_ctx is null || _host is null || _menu is null) return;
        CommitRename();
        Vector2 local = rootPosition - _host.Position;
        _menuRow = Dom.ElementAt(_host, local, ".node");
        if (_menuRow is not null && _menuRow.ClassList.Contains("empty")) _menuRow = null;
        string? kind = _menuRow?.GetAttribute("data-kind");
        _menu.Clear();
        switch (_tab)
        {
            case Tab.Events:
                if (kind == "event")
                {
                    _menu.AddItem("Play", (int)MenuItem.Play);
                    _menu.AddItem("Rename", (int)MenuItem.Rename);
                    _menu.AddItem("Duplicate", (int)MenuItem.Duplicate);
                    _menu.AddItem("Delete", (int)MenuItem.Delete);
                    _menu.AddSeparator();
                }
                else if (kind == "folder")
                {
                    _menu.AddItem("Rename folder", (int)MenuItem.Rename);
                    _menu.AddItem("Delete folder", (int)MenuItem.Delete);
                    _menu.AddSeparator();
                }
                _menu.AddItem("New event", (int)MenuItem.NewEvent);
                _menu.AddItem("New folder", (int)MenuItem.NewFolder);
                break;
            case Tab.Assets:
                if (kind == "asset")
                {
                    _menu.AddItem("Preview", (int)MenuItem.Preview);
                }
                _menu.AddItem("Import lengths", (int)MenuItem.ImportLength);
                break;
            default:
                if (kind is "snapshot" or "parameter" or "bank")
                {
                    _menu.AddItem("Rename", (int)MenuItem.Rename);
                    _menu.AddItem("Delete", (int)MenuItem.Delete);
                    _menu.AddSeparator();
                }
                _menu.AddItem(_tab switch { Tab.Snapshots => "New snapshot", Tab.Parameters => "New parameter", _ => "New bank" }, (int)MenuItem.Add);
                break;
        }
        Vector2 screen = _root.GetScreenPosition() + rootPosition;
        _menu.Position = (Vector2I)screen;
        _menu.ResetSize();
        _menu.Popup();
    }

    private void OnMenuItem(long id)
    {
        if (_ctx is null) return;
        Element? row = _menuRow;
        _menuRow = null;
        string? kind = row?.GetAttribute("data-kind");
        switch ((MenuItem)id)
        {
            case MenuItem.Play:
                if (kind == "event") _ctx.Audition.Play(DirgeId.Parse(row!.GetAttribute("data-id")!));
                break;
            case MenuItem.Rename:
                if (row is not null) BeginRename(row);
                break;
            case MenuItem.Duplicate:
                if (kind == "event") DuplicateEvent(DirgeId.Parse(row!.GetAttribute("data-id")!));
                break;
            case MenuItem.Delete:
                if (row is not null) DeleteRow(row);
                break;
            case MenuItem.NewEvent:
                NewEvent(kind == "folder" ? row!.GetAttribute("data-path")! : kind == "event" ? EventTree.FolderOf(row!.GetAttribute("data-path")!) : SelectedFolder());
                break;
            case MenuItem.NewFolder:
                NewFolder(kind == "folder" ? row!.GetAttribute("data-path")! : kind == "event" ? EventTree.FolderOf(row!.GetAttribute("data-path")!) : SelectedFolder());
                break;
            case MenuItem.Preview:
                if (kind == "asset") PreviewAsset(row!.GetAttribute("data-path")!);
                break;
            case MenuItem.ImportLength:
                ImportLengths();
                break;
            case MenuItem.Add:
                AddItem();
                break;
        }
    }

    private void Confirm(string text, Action action)
    {
        if (_confirm is null) { action(); return; }
        _pendingConfirm = action;
        _confirm.DialogText = text;
        _confirm.PopupCentered();
    }

    private void DeleteRow(Element row)
    {
        string? kind = row.GetAttribute("data-kind");
        switch (kind)
        {
            case "event": DeleteEvent(DirgeId.Parse(row.GetAttribute("data-id")!)); break;
            case "folder": DeleteFolder(row.GetAttribute("data-path")!); break;
            case "snapshot": DeleteSnapshot(DirgeId.Parse(row.GetAttribute("data-id")!)); break;
            case "parameter": DeleteParameter(DirgeId.Parse(row.GetAttribute("data-id")!)); break;
            case "bank": DeleteBank(DirgeId.Parse(row.GetAttribute("data-id")!)); break;
        }
    }

    private void AddItem()
    {
        switch (_tab)
        {
            case Tab.Events: NewEvent(SelectedFolder()); break;
            case Tab.Snapshots: NewSnapshot(); break;
            case Tab.Parameters: NewParameter(); break;
            case Tab.Banks: NewBank(); break;
        }
    }

    // ---- commands -------------------------------------------------------------------------------------------------------

    public bool Handle(ToolCommand command)
    {
        if (_ctx is null) return false;
        switch (command)
        {
            case ToolCommand.Escape:
                if (_rename is null) return false;
                CancelRename();
                return true;
            case ToolCommand.Delete:
            {
                if (_rename is not null) return false;
                Element? row = SelectedRow();
                if (row is null) return false;
                DeleteRow(row);
                return true;
            }
            case ToolCommand.Duplicate:
            {
                if (_tab != Tab.Events || _ctx.Selection.Kind != SelectionKind.Event) return false;
                DuplicateEvent(_ctx.Selection.Id);
                return true;
            }
            default:
                return false;
        }
    }

    private Element? SelectedRow() => _selectedRow;

    // ---- drag and drop ----------------------------------------------------------------------------------------------

    private Variant GetDragData(Vector2 at)
    {
        if (_host is null) return default;
        CommitRename();
        Element? row = Dom.ElementAt(_host, at, ".node");
        DragPayload? payload = row?.GetAttribute("data-kind") switch
        {
            "event" => new EventDrag(DirgeId.Parse(row.GetAttribute("data-id")!)),
            "asset" => new AssetDrag(row.GetAttribute("data-path")!),
            _ => null,
        };
        if (payload is null) return default;
        var preview = new Label { Text = row!.QuerySelector(".name")?.TextContent ?? "" };
        preview.AddThemeColorOverride("font_color", ToolTheme.Current.Fg);
        _host.SetDragPreview(preview);
        return ToolDrag.Wrap(payload);
    }

    private bool CanDropData(Vector2 at, Variant data)
    {
        if (_host is null || _tab != Tab.Events || ToolDrag.Unwrap(data) is not EventDrag) return false;
        Element? row = Dom.ElementAt(_host, at, ".node");
        Element? target = row?.GetAttribute("data-kind") is "folder" or "event" ? row : null;
        if (!ReferenceEquals(target, _dropHover))
        {
            if (_dropHover is not null) Dom.SetClass(_dropHover, "drop-target", false);
            _dropHover = target;
            if (target is not null) Dom.SetClass(target, "drop-target", true);
        }
        return true;
    }

    private void DropData(Vector2 at, Variant data)
    {
        if (_host is null || ToolDrag.Unwrap(data) is not EventDrag drag) return;
        Element? row = Dom.ElementAt(_host, at, ".node");
        string folder = row?.GetAttribute("data-kind") switch
        {
            "folder" => row.GetAttribute("data-path")!,
            "event" => EventTree.FolderOf(row.GetAttribute("data-path")!),
            _ => "",
        };
        MoveEventToFolder(drag.Event, folder);
    }

    private void OnDragEnded()
    {
        if (_dropHover is not null) { Dom.SetClass(_dropHover, "drop-target", false); _dropHover = null; }
        if (_host is null) return;
        // The document never saw the pointer release that ended the drag: drop its pressed/hover state.
        _host.InputEnabled = false;
        _host.InputEnabled = true;
        _doubleClick.Reset();
    }
}

/// <summary>Root control of the browser pane: surfaces right clicks (the document leaves them unhandled) and drag completion.</summary>
internal sealed partial class BrowserRoot : Control
{
    public event Action<Vector2>? ContextRequested;
    public event Action? DragEnded;

    public override void _GuiInput(Godot.InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } button)
        {
            AcceptEvent();
            ContextRequested?.Invoke(button.Position);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationDragEnd) DragEnded?.Invoke();
    }
}
