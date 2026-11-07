#nullable enable
using System;
using System.Globalization;
using Godot;
using NecronomiDom.Dom;
using NecronomiDom.Paint;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Browser;

/// <summary>Small NecronomiDOM conveniences used by the Browser and Transport panes.</summary>
internal static class Dom
{
    /// <summary>Creates an element with optional class list and text content under <paramref name="parent"/>.</summary>
    public static HTMLElement Add(Element parent, string tag, string? className = null, string? text = null)
    {
        var element = (HTMLElement)parent.OwnerDocument!.CreateElement(tag);
        if (className is not null) element.ClassName = className;
        if (text is not null) element.TextContent = text;
        parent.AppendChild(element);
        return element;
    }

    public static void On(EventTarget target, string type, Action<NecronomiDom.Dom.Event> handler)
        => target.AddEventListener(type, new DelegateEventListener(handler));

    /// <summary>Sets text only when it differs (avoids layout invalidation on unchanged readouts).</summary>
    public static void SetText(Element element, string text)
    {
        if (element.TextContent != text) element.TextContent = text;
    }

    public static void SetClass(Element element, string className, bool on)
    {
        if (on) element.ClassList.Add(className);
        else element.ClassList.Remove(className);
    }

    /// <summary>Layout bounds of <paramref name="element"/> in host-local pixels, from the latest display list.</summary>
    public static bool TryGetBounds(DocumentHost host, Element element, out Rect bounds)
    {
        var boxes = host.DisplayList.Boxes;
        for (int i = 0; i < boxes.Count; i++)
        {
            if (ReferenceEquals(boxes[i].Element, element)) { bounds = boxes[i].Bounds; return true; }
        }
        bounds = default;
        return false;
    }

    /// <summary>Topmost element at a host-local point that matches <paramref name="selector"/> (itself or an ancestor).</summary>
    public static Element? ElementAt(DocumentHost host, Vector2 point, string selector)
    {
        var boxes = host.DisplayList.Boxes;
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            LayoutBox box = boxes[i];
            if (!box.Bounds.Contains(point.X, point.Y) || !box.Element.IsConnected) continue;
            Element? match = box.Element.Closest(selector);
            if (match is not null) return match;
        }
        return null;
    }

    /// <summary>Nearest ancestor-or-self of an event's target matching <paramref name="selector"/>.</summary>
    public static Element? Closest(NecronomiDom.Dom.Event e, string selector) => (e.Target as Element)?.Closest(selector);

    public static string F(float value, int decimals = 2) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    public static string F(double value, int decimals = 2) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);

    /// <summary>mm:ss.d for asset lengths and playback positions.</summary>
    public static string Time(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int minutes = (int)(seconds / 60);
        double rest = seconds - minutes * 60;
        return minutes.ToString(CultureInfo.InvariantCulture) + ":" + (rest < 10 ? "0" : "") + rest.ToString("F1", CultureInfo.InvariantCulture);
    }
}

/// <summary>Turns consecutive clicks on the same element within <see cref="WindowMs"/> into a double click (NecronomiDOM dispatches no <c>dblclick</c>).</summary>
internal sealed class DoubleClick
{
    public const ulong WindowMs = 400;
    private Element? _last;
    private ulong _lastMs;

    /// <summary>Returns true when this click completes a double click on <paramref name="target"/>.</summary>
    public bool Register(Element target)
    {
        ulong now = Godot.Time.GetTicksMsec();
        bool second = ReferenceEquals(_last, target) && now - _lastMs <= WindowMs;
        _last = second ? null : target;
        _lastMs = now;
        return second;
    }

    public void Reset() => _last = null;
}
