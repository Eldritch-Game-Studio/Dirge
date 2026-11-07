#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using Dirge.Validation;
using Godot;
using NecronomiDom.Dom;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Shell;

/// <summary>Diagnostics of the open project; clicking a row selects the offending object.</summary>
public sealed class ValidationPanel : IToolPane
{
    private readonly Control _root;
    private readonly DocumentHost _host;
    private readonly List<Diagnostic> _shown = new();
    private readonly List<Element> _rows = new();
    private ToolContext? _context;
    private Element _list = null!;
    private Element _summary = null!;
    private Element _empty = null!;
    private int _selectedRow = -1;

    public ValidationPanel()
    {
        _root = new Control
        {
            Name = "Validation",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 80),
        };
        _host = ToolDocuments.Create("validation.html");
        _root.AddChild(_host);
    }

    public string Title => "Validation";
    public Control Root => _root;
    public int ErrorCount { get; private set; }
    public int WarningCount { get; private set; }
    public IReadOnlyList<Diagnostic> Diagnostics => _shown;

    /// <summary>Raised after every refresh with the current counts.</summary>
    public event Action<int, int>? CountsChanged;

    public void Attach(ToolContext context)
    {
        _context = context;
        Document document = _host.Document;
        _list = Required(document, "list");
        _summary = Required(document, "summary");
        _empty = Required(document, "empty");
        Required(document, "revalidate").AddEventListener("click", new DelegateEventListener(_ => Refresh(force: true)));
        context.Project.Changed += OnProjectChanged;
        context.Selection.Changed += OnSelectionChanged;
        Refresh(force: true);
    }

    public void Detach()
    {
        if (_context is not null)
        {
            _context.Project.Changed -= OnProjectChanged;
            _context.Selection.Changed -= OnSelectionChanged;
        }

        _context = null;
    }

    private void OnProjectChanged(ChangeScope scope, DirgeId subject) => Refresh(force: false);

    /// <summary>Re-validates; rebuilds the rows only when the diagnostics actually changed.</summary>
    public void Refresh(bool force)
    {
        if (_context is null)
        {
            return;
        }

        IReadOnlyList<Diagnostic> diagnostics = _context.Project.Validate();
        if (!force && SameAsShown(diagnostics))
        {
            return;
        }

        _shown.Clear();
        _shown.AddRange(diagnostics);
        ErrorCount = 0;
        WarningCount = 0;
        foreach (Diagnostic diagnostic in _shown)
        {
            if (diagnostic.Severity == Severity.Error)
            {
                ErrorCount++;
            }
            else
            {
                WarningCount++;
            }
        }

        RebuildRows();
        _summary.TextContent = _shown.Count == 0 ? "No issues" : $"{ErrorCount} error{(ErrorCount == 1 ? "" : "s")} · {WarningCount} warning{(WarningCount == 1 ? "" : "s")}";
        ((HTMLElement)_empty).Hidden = _shown.Count > 0;
        ((HTMLElement)_list).Hidden = _shown.Count == 0;
        CountsChanged?.Invoke(ErrorCount, WarningCount);
    }

    private bool SameAsShown(IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Count != _shown.Count)
        {
            return false;
        }

        for (int i = 0; i < diagnostics.Count; i++)
        {
            if (diagnostics[i] != _shown[i])
            {
                return false;
            }
        }

        return true;
    }

    private void RebuildRows()
    {
        Document document = _host.Document;
        AudioProject project = _context!.Project.Project;
        _list.ReplaceChildren();
        _rows.Clear();
        _selectedRow = -1;
        for (int i = 0; i < _shown.Count; i++)
        {
            Diagnostic diagnostic = _shown[i];
            SubjectLookup.Subject subject = SubjectLookup.Resolve(project, diagnostic.Subject);
            Element row = document.CreateElement("div");
            row.ClassName = "diagnostic " + (diagnostic.Severity == Severity.Error ? "error" : "warning") + (subject.IsNone ? " unselectable" : "");
            row.SetAttribute("role", "listitem");
            row.Append(
                Text(document, "severity", diagnostic.Severity == Severity.Error ? "ERROR" : "WARNING"),
                Text(document, "code", diagnostic.Code),
                Text(document, "message", diagnostic.Message),
                Text(document, "subject", subject.IsNone ? "project" : subject.Name));
            int index = i;
            row.AddEventListener("click", new DelegateEventListener(_ => OnRowClicked(index)));
            _list.Append(row);
            _rows.Add(row);
        }
    }

    private void OnRowClicked(int index)
    {
        if (_context is null || index >= _shown.Count)
        {
            return;
        }

        SubjectLookup.Subject subject = SubjectLookup.Resolve(_context.Project.Project, _shown[index].Subject);
        SelectRow(index);
        if (!subject.IsNone)
        {
            _context.Selection.Select(subject.Kind, subject.Id, subject.Owner, subject.Index);
        }
    }

    private void OnSelectionChanged()
    {
        if (_context is null)
        {
            return;
        }

        // Keep the highlighted row in sync with a selection made elsewhere.
        DirgeId id = _context.Selection.Id;
        for (int i = 0; i < _shown.Count; i++)
        {
            if (!id.IsNone && _shown[i].Subject == id)
            {
                SelectRow(i);
                return;
            }
        }

        SelectRow(-1);
    }

    private void SelectRow(int index)
    {
        if (_selectedRow == index)
        {
            return;
        }

        if (_selectedRow >= 0 && _selectedRow < _rows.Count)
        {
            _rows[_selectedRow].ClassList.Remove("selected");
        }

        _selectedRow = index;
        if (index >= 0 && index < _rows.Count)
        {
            _rows[index].ClassList.Add("selected");
        }
    }

    private static Element Text(Document document, string className, string text)
    {
        Element element = document.CreateElement("p");
        element.ClassName = className;
        element.TextContent = text;
        return element;
    }

    private static Element Required(Document document, string id)
        => document.GetElementById(id) ?? throw new InvalidOperationException($"validation.html must keep id='{id}'.");
}
