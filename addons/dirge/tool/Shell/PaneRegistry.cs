#nullable enable
using System;
using System.Reflection;
using Dirge.Tool.Core;
using Godot;

namespace Dirge.Tool.Shell;

/// <summary>
/// Resolves panes by fully qualified type name so the shell compiles and runs before (or without) the pane slices.
/// A missing pane is replaced by a visible placeholder.
/// </summary>
public static class PaneRegistry
{
    public const string Mixer = "Dirge.Tool.Mixer.MixerPane";
    public const string Events = "Dirge.Tool.Events.EventEditorPane";
    public const string Browser = "Dirge.Tool.Browser.BrowserPane";
    public const string Transport = "Dirge.Tool.Transport.TransportPane";
    public const string Profiler = "Dirge.Tool.Profiler.ProfilerPane";

    /// <summary>Instantiates the pane or a <see cref="MissingPane"/> placeholder; never throws.</summary>
    public static IToolPane Create(string typeName, string title)
    {
        Type? type = Resolve(typeName);
        if (type is null)
        {
            return new MissingPane(title, typeName, "type not found");
        }

        if (!typeof(IToolPane).IsAssignableFrom(type))
        {
            return new MissingPane(title, typeName, "does not implement IToolPane");
        }

        try
        {
            if (Activator.CreateInstance(type) is IToolPane pane)
            {
                return pane;
            }

            return new MissingPane(title, typeName, "no parameterless constructor");
        }
        catch (Exception error)
        {
            GD.PushError($"Dirge: creating pane '{typeName}' failed: {error}");
            return new MissingPane(title, typeName, error.GetBaseException().Message);
        }
    }

    public static bool Exists(string typeName) => Resolve(typeName) is not null;

    private static Type? Resolve(string typeName)
    {
        Type? type = Type.GetType(typeName, throwOnError: false) ?? typeof(PaneRegistry).Assembly.GetType(typeName, throwOnError: false);
        if (type is not null)
        {
            return type;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            type = assembly.GetType(typeName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }
}

/// <summary>Placeholder shown where a pane slice has not landed yet.</summary>
public sealed class MissingPane : IToolPane
{
    private readonly Control _root;

    public MissingPane(string title, string typeName, string reason)
    {
        Title = title;
        TypeName = typeName;
        // A plain Control root keeps the placeholder's minimum size at zero so the wrapped label never dictates the layout.
        _root = new Control
        {
            Name = title.Replace(' ', '_') + "Missing",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ClipContents = true,
        };
        var label = new Label
        {
            Name = "Label",
            Text = $"{title} pane missing\n{typeName}\n({reason})",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ClipText = true,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeColorOverride("font_color", ToolTheme.Current.Muted);
        _root.AddChild(label);
    }

    public string Title { get; }
    public string TypeName { get; }
    public Control Root => _root;

    public void Attach(ToolContext context) { }
    public void Detach() { }
}
