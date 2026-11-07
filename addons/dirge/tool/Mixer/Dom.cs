#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using NecronomiDom.Dom;
using NecronomiDom.Paint;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Mixer;

/// <summary>Small DOM construction/lookup helpers shared by the mixer views. Listener registrations are tracked so a view can
/// detach cleanly when the document goes away.</summary>
internal sealed class Dom
{
    private readonly List<(EventTarget Target, string Type, IEventListener Listener)> _listeners = new();
    private static readonly string[] PixelStrings = BuildPixelStrings(400);

    public DocumentHost Host { get; }
    public Document Document => Host.Document;

    public Dom(DocumentHost host) => Host = host;

    public HTMLElement El(string tag, string? className = null, string? text = null)
    {
        var element = (HTMLElement)Document.CreateElement(tag);
        if (className is not null) element.ClassName = className;
        if (text is not null) element.TextContent = text;
        return element;
    }

    public HTMLElement El(HTMLElement parent, string tag, string? className = null, string? text = null)
    {
        HTMLElement element = El(tag, className, text);
        parent.AppendChild(element);
        return element;
    }

    public HTMLInputElement Input(HTMLElement parent, string className, string value)
    {
        var input = (HTMLInputElement)Document.CreateElement("input");
        input.SetAttribute("type", "text");
        input.ClassName = className;
        input.Value = value;
        parent.AppendChild(input);
        return input;
    }

    public HTMLElement Required(string id) => Document.GetElementById(id) as HTMLElement
        ?? throw new InvalidOperationException($"mixer.html must keep id='{id}'.");

    public void On(EventTarget target, string type, Action<Event> handler)
    {
        var listener = new DelegateEventListener(handler);
        target.AddEventListener(type, listener);
        _listeners.Add((target, type, listener));
    }

    public void On(EventTarget target, string type, Action handler) => On(target, type, _ => handler());

    public void DetachAll()
    {
        for (int i = 0; i < _listeners.Count; i++) _listeners[i].Target.RemoveEventListener(_listeners[i].Type, _listeners[i].Listener);
        _listeners.Clear();
    }

    /// <summary>Layout bounds of <paramref name="element"/> from the last layout pass, in document coordinates.</summary>
    public bool TryGetBounds(Element element, out Rect bounds)
    {
        List<LayoutBox> boxes = Host.DisplayList.Boxes;
        for (int i = 0; i < boxes.Count; i++)
        {
            if (ReferenceEquals(boxes[i].Element, element)) { bounds = boxes[i].Bounds; return true; }
        }
        bounds = default;
        return false;
    }

    public Element? HitTest(double x, double y) => Host.DisplayList.HitTest(x, y);

    /// <summary>Nearest ancestor-or-self with <paramref name="className"/>.</summary>
    public static HTMLElement? Closest(Element? start, string className)
    {
        for (Element? current = start; current is not null; current = current.ParentElement)
        {
            if (current is HTMLElement html && current.ClassList.Contains(className)) return html;
        }
        return null;
    }

    /// <summary>"123px" without allocating for integer pixel values in range.</summary>
    public static string Px(int pixels)
    {
        if (pixels >= 0 && pixels < PixelStrings.Length) return PixelStrings[pixels];
        return pixels.ToString(CultureInfo.InvariantCulture) + "px";
    }

    public static void SetClass(Element element, string className, bool on)
    {
        if (on) { if (!element.ClassList.Contains(className)) element.ClassList.Add(className); }
        else if (element.ClassList.Contains(className)) element.ClassList.Remove(className);
    }

    public static void SetText(HTMLElement element, string text)
    {
        if (element.TextContent != text) element.TextContent = text;
    }

    public static string Db(float value) =>
        float.IsNegativeInfinity(value) || value <= -80f ? "-inf dB" : value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";

    public static string Num(float value, float step)
    {
        if (step >= 1f) return ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture);
        if (step >= 0.1f) return value.ToString("0.0", CultureInfo.InvariantCulture);
        if (step >= 0.01f) return value.ToString("0.00", CultureInfo.InvariantCulture);
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string text, out float value) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private static string[] BuildPixelStrings(int count)
    {
        var strings = new string[count];
        for (int i = 0; i < count; i++) strings[i] = i.ToString(CultureInfo.InvariantCulture) + "px";
        return strings;
    }
}
