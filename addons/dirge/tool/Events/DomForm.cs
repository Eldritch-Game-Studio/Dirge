#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using NecronomiDom.Dom;
using DomEvent = NecronomiDom.Dom.Event;

namespace Dirge.Tool.Events;

/// <summary>
/// Builds dense property forms inside a NecronomiDOM document using only the admitted element profile
/// (<c>div/span/p/button/input</c>). Values are committed on the <c>change</c> event (Enter/blur) so typing never
/// re-renders the form mid-edit. Dropdowns are in-document <see cref="DomMenu"/> popups.
/// </summary>
internal sealed class DomForm
{
    private readonly Document _document;

    public DomForm(Document document) => _document = document;

    public Document Document => _document;

    public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public Element Div(Element parent, string? className = null, string? id = null)
    {
        Element div = _document.CreateElement("div");
        if (className is not null) div.ClassName = className;
        if (id is not null) div.Id = id;
        parent.AppendChild(div);
        return div;
    }

    public Element Span(Element parent, string text, string? className = null)
    {
        Element span = _document.CreateElement("span");
        if (className is not null) span.ClassName = className;
        span.TextContent = text;
        parent.AppendChild(span);
        return span;
    }

    public Element Note(Element parent, string text)
    {
        Element p = _document.CreateElement("p");
        p.ClassName = "note";
        p.TextContent = text;
        parent.AppendChild(p);
        return p;
    }

    /// <summary>Titled section; returns the body container.</summary>
    public Element Section(Element parent, string title)
    {
        Element section = Div(parent, "section");
        Element head = Div(section, "section-head");
        Span(head, title, "section-title");
        return Div(section, "section-body");
    }

    public Element Row(Element parent, string label)
    {
        Element row = Div(parent, "row");
        Span(row, label, "label");
        return row;
    }

    public Element Button(Element parent, string label, Action click, string? className = null)
    {
        Element button = _document.CreateElement("button");
        button.ClassName = className ?? "btn";
        button.TextContent = label;
        On(button, "click", _ => click());
        parent.AppendChild(button);
        return button;
    }

    public HTMLInputElement Text(Element parent, string label, string value, Action<string> commit, string? placeholder = null)
    {
        Element row = Row(parent, label);
        return TextInput(row, value, commit, placeholder);
    }

    public HTMLInputElement TextInput(Element parent, string value, Action<string> commit, string? placeholder = null)
    {
        var input = (HTMLInputElement)_document.CreateElement("input");
        input.Type = "text";
        input.ClassName = "field";
        input.Value = value;
        if (placeholder is not null) input.SetAttribute("placeholder", placeholder);
        On(input, "change", _ => commit(input.Value));
        parent.AppendChild(input);
        return input;
    }

    public HTMLInputElement Number(Element parent, string label, double value, Action<double> commit, string? unit = null, string format = "0.###")
    {
        Element row = Row(parent, label);
        HTMLInputElement input = NumberInput(row, value, commit, format);
        if (unit is not null) Span(row, unit, "unit");
        return input;
    }

    public HTMLInputElement NumberInput(Element parent, double value, Action<double> commit, string format = "0.###")
    {
        var input = (HTMLInputElement)_document.CreateElement("input");
        input.Type = "text";
        input.ClassName = "field num";
        input.Value = value.ToString(format, Culture);
        On(input, "change", _ =>
        {
            if (double.TryParse(input.Value.Trim(), NumberStyles.Float, Culture, out double parsed) && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
                commit(parsed);
            else input.Value = value.ToString(format, Culture);
        });
        parent.AppendChild(input);
        return input;
    }

    public HTMLInputElement Int(Element parent, string label, int value, Action<int> commit, string? unit = null)
    {
        Element row = Row(parent, label);
        HTMLInputElement input = IntInput(row, value, commit);
        if (unit is not null) Span(row, unit, "unit");
        return input;
    }

    public HTMLInputElement IntInput(Element parent, int value, Action<int> commit)
    {
        var input = (HTMLInputElement)_document.CreateElement("input");
        input.Type = "text";
        input.ClassName = "field num";
        input.Value = value.ToString(Culture);
        On(input, "change", _ =>
        {
            if (int.TryParse(input.Value.Trim(), NumberStyles.Integer, Culture, out int parsed)) commit(parsed);
            else input.Value = value.ToString(Culture);
        });
        parent.AppendChild(input);
        return input;
    }

    public Element Toggle(Element parent, string label, bool value, Action<bool> commit)
    {
        Element row = Row(parent, label);
        return ToggleButton(row, value ? "On" : "Off", value, commit);
    }

    public Element ToggleButton(Element parent, string text, bool value, Action<bool> commit)
    {
        Element button = _document.CreateElement("button");
        button.ClassName = value ? "toggle on" : "toggle";
        button.TextContent = text;
        On(button, "click", _ => commit(!value));
        parent.AppendChild(button);
        return button;
    }

    /// <summary>Dropdown: a button showing the current option; click opens an in-document menu.</summary>
    public Element Choice(Element parent, string label, IReadOnlyList<string> options, int selected, Action<int> commit)
    {
        Element row = Row(parent, label);
        return ChoiceButton(row, options, selected, commit);
    }

    public Element ChoiceButton(Element parent, IReadOnlyList<string> options, int selected, Action<int> commit, string? emptyText = null)
    {
        Element button = _document.CreateElement("button");
        button.ClassName = "choice";
        button.TextContent = selected >= 0 && selected < options.Count ? options[selected] : emptyText ?? "(none)";
        On(button, "click", e =>
        {
            var mouse = e as MouseEvent;
            DomMenu.Show(_document, mouse?.ClientX ?? 0, mouse?.ClientY ?? 0, options, selected, commit);
        });
        parent.AppendChild(button);
        return button;
    }

    public Element EnumChoice<T>(Element parent, string label, T value, Action<T> commit) where T : struct, Enum
    {
        T[] values = Enum.GetValues<T>();
        var names = new string[values.Length];
        int selected = 0;
        for (int i = 0; i < values.Length; i++)
        {
            names[i] = values[i].ToString();
            if (EqualityComparer<T>.Default.Equals(values[i], value)) selected = i;
        }
        return Choice(parent, label, names, selected, i => commit(values[i]));
    }

    public static void On(EventTarget target, string type, Action<DomEvent> handler) => target.AddEventListener(type, new DelegateEventListener(handler));

    public static string F(float value, string format = "0.###") => value.ToString(format, Culture);
}

/// <summary>In-document popup list anchored at a client position; closes on pick or on any pointer press outside.</summary>
internal static class DomMenu
{
    public static void Show(Document document, double x, double y, IReadOnlyList<string> options, int selected, Action<int> pick)
    {
        Close(document);
        Element body = document.Body ?? throw new InvalidOperationException("Document has no body.");
        Element menu = document.CreateElement("div");
        menu.ClassName = "menu";
        menu.Id = "dom-menu";
        var html = (HTMLElement)menu;
        html.Style.SetProperty("left", Math.Max(0, x - 8).ToString("0", CultureInfo.InvariantCulture) + "px");
        html.Style.SetProperty("top", Math.Max(0, y - 10).ToString("0", CultureInfo.InvariantCulture) + "px");
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            Element item = document.CreateElement("button");
            item.ClassName = i == selected ? "menu-item on" : "menu-item";
            item.TextContent = options[i];
            DomForm.On(item, "click", e => { e.StopPropagation(); Close(document); pick(index); });
            menu.AppendChild(item);
        }
        if (options.Count == 0)
        {
            Element empty = document.CreateElement("span");
            empty.ClassName = "menu-empty";
            empty.TextContent = "(nothing to pick)";
            menu.AppendChild(empty);
        }
        body.AppendChild(menu);
        var closer = new Closer(document, menu);
        document.AddEventListener("pointerdown", closer, new EventListenerOptions { Capture = true });
        closer.Listener = closer;
    }

    public static void Close(Document document)
    {
        if (document.GetElementById("dom-menu") is { } existing) existing.Remove();
    }

    private sealed class Closer : IEventListener
    {
        private readonly Document _document;
        private readonly Element _menu;
        public IEventListener? Listener;
        public Closer(Document document, Element menu) { _document = document; _menu = menu; }

        public void HandleEvent(DomEvent e)
        {
            bool detached = _menu.ParentElement is null;
            if (!detached && e.Target is Node target && _menu.Contains(target)) return;
            if (!detached) _menu.Remove();
            _document.RemoveEventListener("pointerdown", Listener, new EventListenerOptions { Capture = true });
        }
    }
}
