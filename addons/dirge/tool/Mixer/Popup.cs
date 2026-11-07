#nullable enable
using System;
using System.Collections.Generic;
using NecronomiDom.Dom;

namespace Dirge.Tool.Mixer;

/// <summary>Custom dropdown: an absolutely positioned list of buttons inside <c>#popup</c>. A pointer press outside closes it.</summary>
internal sealed class Popup
{
    public readonly struct Item
    {
        public readonly string Label;
        public readonly bool Current;
        public readonly bool On;
        public readonly Action Action;
        public Item(string label, Action action, bool current = false, bool on = false) { Label = label; Action = action; Current = current; On = on; }
    }

    private readonly Dom _dom;
    private readonly HTMLElement _root;
    private readonly List<(HTMLElement Element, DelegateEventListener Listener)> _itemListeners = new();
    private bool _keepOpenOnSelect;

    public bool IsOpen => !_root.Hidden;
    public HTMLElement Root => _root;

    public Popup(Dom dom, HTMLElement root)
    {
        _dom = dom; _root = root;
        _dom.On(_dom.Document, "pointerdown", OnDocumentPointerDown);
    }

    /// <summary>Show <paramref name="items"/> near (<paramref name="x"/>, <paramref name="y"/>) in document coordinates.</summary>
    public void Show(string title, IReadOnlyList<Item> items, double x, double y, bool keepOpenOnSelect = false)
    {
        Clear();
        _keepOpenOnSelect = keepOpenOnSelect;
        if (title.Length > 0) _dom.El(_root, "div", "popup-title", title);
        for (int i = 0; i < items.Count; i++)
        {
            Item item = items[i];
            HTMLElement button = _dom.El(_root, "button", "popup-item", item.Label);
            if (item.Current) button.ClassList.Add("current");
            if (item.On) button.ClassList.Add("on");
            var listener = new DelegateEventListener(_ =>
            {
                item.Action();
                if (!_keepOpenOnSelect) Hide();
            });
            button.AddEventListener("click", listener);
            _itemListeners.Add((button, listener));
        }
        if (items.Count == 0) _dom.El(_root, "div", "popup-title", "(nothing available)");
        Place(x, y, 200, Math.Min(260, 8 + (title.Length > 0 ? 21 : 0) + Math.Max(1, items.Count) * 20));
        _root.Hidden = false;
    }

    public void Hide()
    {
        if (_root.Hidden) return;
        _root.Hidden = true;
        Clear();
    }

    private void Place(double x, double y, double width, double height)
    {
        double maxX = Math.Max(0, _dom.Host.Size.X - width - 2);
        double maxY = Math.Max(0, _dom.Host.Size.Y - height - 2);
        _root.Style.SetProperty("left", Dom.Px((int)Math.Clamp(x, 0, maxX)));
        _root.Style.SetProperty("top", Dom.Px((int)Math.Clamp(y, 0, maxY)));
    }

    private void Clear()
    {
        for (int i = 0; i < _itemListeners.Count; i++) _itemListeners[i].Element.RemoveEventListener("click", _itemListeners[i].Listener);
        _itemListeners.Clear();
        _root.TextContent = "";
    }

    private void OnDocumentPointerDown(Event e)
    {
        if (_root.Hidden) return;
        for (Node? node = e.Target as Node; node is not null; node = node.ParentNode)
        {
            if (ReferenceEquals(node, _root)) return;
        }
        Hide();
    }
}
