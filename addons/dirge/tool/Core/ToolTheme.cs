#nullable enable
using System;
using System.Text;
using Godot;

namespace Dirge.Tool.Core;

/// <summary>Theme presets selectable through the <c>dirge/theme</c> editor setting (stored as the enum index).</summary>
public enum ToolThemePreset
{
    /// <summary>Derived from the Godot editor theme (base/accent/font colors), so Dirge follows light editor themes too.</summary>
    Editor = 0,
    DirgeDark = 1,
    DirgeLight = 2,
    HighContrast = 3,
    /// <summary>Background/foreground/accent from <c>dirge/theme_background</c>, <c>dirge/theme_foreground</c>, <c>dirge/theme_accent</c>.</summary>
    Custom = 4,
}

/// <summary>
/// A named palette: every color custom property the tool documents consume (<c>theme.css</c> lists the same names with the
/// Dirge Dark values as defaults) plus the colors the Godot canvases draw with. Palettes other than Dirge Dark are derived
/// from a background, foreground and accent with <see cref="Derive"/>, which keeps text contrast at or above 4.5:1.
/// </summary>
public sealed record ToolPalette
{
    public required string Name { get; init; }
    /// <summary>True when the background is dark; derived colors step toward white, otherwise toward black.</summary>
    public required bool Dark { get; init; }

    // Surfaces
    public required Color Bg { get; init; }
    public required Color Panel { get; init; }
    public required Color Panel2 { get; init; }
    public required Color PanelMaster { get; init; }
    public required Color PanelSnapshot { get; init; }
    public required Color Hover { get; init; }
    public required Color Selection { get; init; }
    public required Color Border { get; init; }
    public required Color Grid { get; init; }
    public required Color GridStrong { get; init; }
    public required Color MeterBg { get; init; }

    // Text
    public required Color Fg { get; init; }
    public required Color Muted { get; init; }
    /// <summary>Text on saturated dark fills (instrument blocks, snapshot chips).</summary>
    public required Color OnDark { get; init; }
    /// <summary>Text on saturated light fills (accent buttons, yellow pills).</summary>
    public required Color OnLight { get; init; }

    // Accent and semantics
    public required Color Accent { get; init; }
    public required Color AccentFg { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentDim { get; init; }
    public required Color Danger { get; init; }
    public required Color DangerDim { get; init; }
    public required Color Warning { get; init; }
    public required Color Success { get; init; }
    public required Color Info { get; init; }

    // Meters
    public required Color MeterLow { get; init; }
    public required Color MeterMid { get; init; }
    public required Color MeterHigh { get; init; }
    public required Color MeterHold { get; init; }

    // Canvas marks
    public required Color Playhead { get; init; }
    public required Color Cursor { get; init; }
    /// <summary>Outline of selected blocks/markers/bars on canvases.</summary>
    public required Color SelectionOutline { get; init; }

    // Mixer
    public required Color Snapshot { get; init; }
    public required Color SnapshotFg { get; init; }
    public required Color Fader { get; init; }
    public required Color Mute { get; init; }
    public required Color Solo { get; init; }
    public required Color Bypass { get; init; }

    // Instrument types (8) plus marker/track chips
    public required Color TypeClip { get; init; }
    public required Color TypeMulti { get; init; }
    public required Color TypeScatterer { get; init; }
    public required Color TypeSilence { get; init; }
    public required Color TypeEvent { get; init; }
    public required Color TypeProgrammer { get; init; }
    public required Color TypeCommand { get; init; }
    public required Color TypeSnapshot { get; init; }
    public required Color TypeMarker { get; init; }
    public required Color TypeTrack { get; init; }
    /// <summary>Fallback for instruments outside the eight known types.</summary>
    public required Color TypeOther { get; init; }

    // Logic markers
    public required Color MarkerLoop { get; init; }
    public required Color MarkerTransition { get; init; }
    public required Color MarkerSustain { get; init; }
    public required Color MarkerDestination { get; init; }
    public required Color MarkerMagnet { get; init; }
    public required Color MarkerOther { get; init; }

    // Profiler
    public required Color BarPlaying { get; init; }
    public required Color BarStopping { get; init; }
    public required Color BarVirtual { get; init; }

    /// <summary>Inline declaration list (<c>--bg: #rrggbb; …</c>) for the document root; names match <c>theme.css</c>.</summary>
    public string CssText
    {
        get
        {
            var sb = new StringBuilder(1400);
            Var(sb, "--bg", Bg); Var(sb, "--panel", Panel); Var(sb, "--panel-2", Panel2); Var(sb, "--panel-master", PanelMaster); Var(sb, "--panel-snapshot", PanelSnapshot);
            Var(sb, "--hover", Hover); Var(sb, "--selection", Selection); Var(sb, "--border", Border); Var(sb, "--grid", Grid); Var(sb, "--grid-strong", GridStrong); Var(sb, "--meter-bg", MeterBg);
            Var(sb, "--fg", Fg); Var(sb, "--muted", Muted); Var(sb, "--on-dark", OnDark); Var(sb, "--on-light", OnLight);
            Var(sb, "--accent", Accent); Var(sb, "--accent-fg", AccentFg); Var(sb, "--accent-hover", AccentHover); Var(sb, "--accent-dim", AccentDim);
            Var(sb, "--danger", Danger); Var(sb, "--danger-dim", DangerDim); Var(sb, "--warning", Warning); Var(sb, "--success", Success); Var(sb, "--info", Info);
            Var(sb, "--meter-low", MeterLow); Var(sb, "--meter-mid", MeterMid); Var(sb, "--meter-high", MeterHigh); Var(sb, "--meter-hold", MeterHold);
            Var(sb, "--playhead", Playhead); Var(sb, "--cursor", Cursor);
            Var(sb, "--snapshot", Snapshot); Var(sb, "--snapshot-fg", SnapshotFg); Var(sb, "--fader", Fader); Var(sb, "--mute", Mute); Var(sb, "--solo", Solo); Var(sb, "--bypass", Bypass);
            Var(sb, "--type-clip", TypeClip); Var(sb, "--type-multi", TypeMulti); Var(sb, "--type-scatterer", TypeScatterer); Var(sb, "--type-silence", TypeSilence);
            Var(sb, "--type-event", TypeEvent); Var(sb, "--type-programmer", TypeProgrammer); Var(sb, "--type-command", TypeCommand); Var(sb, "--type-snapshot", TypeSnapshot);
            Var(sb, "--type-marker", TypeMarker); Var(sb, "--type-track", TypeTrack);
            return sb.ToString();
        }
    }

    private static void Var(StringBuilder sb, string name, Color color) => sb.Append(name).Append(": ").Append(Hex(color)).Append("; ");

    /// <summary>Opaque <c>#rrggbb</c>.</summary>
    public static string Hex(Color color) => "#" + color.ToHtml(false);

    // ---- color math ----------------------------------------------------------------------------------------------------

    private static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    public static float Luminance(Color c) => 0.2126f * Linear(c.R) + 0.7152f * Linear(c.G) + 0.0722f * Linear(c.B);

    /// <summary>WCAG contrast ratio, ≥ 1.</summary>
    public static float Contrast(Color a, Color b)
    {
        float la = Luminance(a) + 0.05f, lb = Luminance(b) + 0.05f;
        return la > lb ? la / lb : lb / la;
    }

    public static bool IsDark(Color bg) => Luminance(bg) < 0.2f;

    /// <summary>Steps <paramref name="color"/> toward white (dark backgrounds) or black until it contrasts ≥ <paramref name="minimum"/> with <paramref name="against"/>.</summary>
    public static Color EnsureContrast(Color color, Color against, float minimum)
    {
        if (Contrast(color, against) >= minimum) return color;
        Color target = IsDark(against) ? Colors.White : Colors.Black;
        Color c = color with { A = 1f };
        for (int i = 1; i <= 32; i++)
        {
            Color candidate = color.Lerp(target, i / 32f);
            c = candidate;
            if (Contrast(candidate, against) >= minimum) break;
        }
        return c;
    }

    /// <summary>Composites <paramref name="color"/> over an opaque <paramref name="bg"/> and drops the alpha.</summary>
    public static Color Flatten(Color color, Color bg) => color.A >= 1f ? color : bg.Lerp(color with { A = 1f }, color.A) with { A = 1f };

    /// <summary>Whichever of <paramref name="light"/>/<paramref name="dark"/> contrasts more with <paramref name="fill"/>.</summary>
    public static Color OnFill(Color fill, Color light, Color dark) => Contrast(dark, fill) >= Contrast(light, fill) ? dark : light;

    // ---- presets ---------------------------------------------------------------------------------------------------------

    /// <summary>The dark look the tool shipped with; also the defaults in <c>theme.css</c>.</summary>
    public static readonly ToolPalette DirgeDark = new()
    {
        Name = "Dirge Dark",
        Dark = true,
        Bg = new Color("1b1d21"), Panel = new Color("24272c"), Panel2 = new Color("2c3036"), PanelMaster = new Color("24262e"), PanelSnapshot = new Color("23202c"),
        Hover = new Color("30353c"), Selection = new Color("3b4b63"), Border = new Color("3a3f47"), Grid = new Color("2f3238"), GridStrong = new Color("565b66"), MeterBg = new Color("15171a"),
        Fg = new Color("d8dbe0"), Muted = new Color("8a9099"), OnDark = new Color("ffffff"), OnLight = new Color("1b1d21"),
        Accent = new Color("e8a33d"), AccentFg = new Color("1b1d21"), AccentHover = new Color("f0b355"), AccentDim = new Color("2d5f91"),
        Danger = new Color("e0584f"), DangerDim = new Color("5a1f1f"), Warning = new Color("e3b341"), Success = new Color("4caf50"), Info = new Color("4fb0ff"),
        MeterLow = new Color("4caf50"), MeterMid = new Color("e3b341"), MeterHigh = new Color("e0584f"), MeterHold = new Color("e6e8ef"),
        Playhead = new Color("ff4b4b"), Cursor = new Color("9ad0ff"), SelectionOutline = new Color("ffffff"),
        Snapshot = new Color("7a4bd6"), SnapshotFg = new Color("c9b3ff"), Fader = new Color("b8bcc8"), Mute = new Color("c9a227"), Solo = new Color("3fb950"), Bypass = new Color("d26b2f"),
        TypeClip = new Color("3d7bd9"), TypeMulti = new Color("8a5bd6"), TypeScatterer = new Color("3fa36a"), TypeSilence = new Color("5c6068"),
        TypeEvent = new Color("e0853a"), TypeProgrammer = new Color("2fa8a3"), TypeCommand = new Color("c9b43a"), TypeSnapshot = new Color("d65b9b"),
        TypeMarker = new Color("4fb0ff"), TypeTrack = new Color("8a9099"), TypeOther = new Color("777777"),
        MarkerLoop = new Color("4fb0ff"), MarkerTransition = new Color("ffb347"), MarkerSustain = new Color("ff6b6b"), MarkerDestination = new Color("7ee787"), MarkerMagnet = new Color("d2a8ff"), MarkerOther = new Color("aaaaaa"),
        BarPlaying = new Color("3f8f5a"), BarStopping = new Color("8f6a3f"), BarVirtual = new Color("4a4d57"),
    };

    public static readonly ToolPalette DirgeLight = Derive("Dirge Light", new Color("f3f4f6"), new Color("1f2328"), new Color("b85c00"));

    public static readonly ToolPalette HighContrast = Derive("High Contrast", Colors.Black, Colors.White, new Color("ffd54a"), highContrast: true);

    /// <summary>
    /// Builds a palette from a background, foreground and accent. <paramref name="fg"/> is replaced by white or black when it
    /// contrasts below 4.5:1 with <paramref name="bg"/> (<paramref name="adjusted"/> reports it); the accent and semantic
    /// colors are pushed to ≥ 3:1 against the background. Surfaces, borders and grid lines are luminance steps from the
    /// background toward the foreground side; <paramref name="highContrast"/> widens those steps.
    /// </summary>
    public static ToolPalette Derive(string name, Color bg, Color fg, Color accent, out bool adjusted, Color? danger = null, Color? warning = null, Color? success = null, Color? info = null, bool highContrast = false)
    {
        bg = bg with { A = 1f };
        fg = Flatten(fg, bg);
        accent = Flatten(accent, bg);
        bool dark = IsDark(bg);
        Color far = dark ? Colors.White : Colors.Black;
        adjusted = false;
        if (Contrast(fg, bg) < 4.5f)
        {
            fg = OnFill(bg, Colors.White, Colors.Black);
            adjusted = true;
        }
        Color accentBefore = accent;
        accent = EnsureContrast(accent, bg, 3f);
        adjusted |= accent != accentBefore;

        float boost = highContrast ? 2f : 1f;
        Color Step(float t) => bg.Lerp(far, Math.Clamp(t * boost, 0f, 1f));
        Color Semantic(Color seed) => EnsureContrast(seed, bg, highContrast ? 4.5f : 3f);

        Color panel = Step(0.04f);
        Color panel2 = Step(0.075f);
        Color border = Step(0.135f);
        Color muted = EnsureContrast(bg.Lerp(fg, highContrast ? 0.8f : 0.58f), panel2, highContrast ? 7f : 3.5f);
        Color dangerC = Semantic(danger ?? new Color("e0584f"));
        Color warningC = Semantic(warning ?? new Color("e3b341"));
        Color successC = Semantic(success ?? new Color("4caf50"));
        Color infoC = Semantic(info ?? new Color("4fb0ff"));
        Color snapshot = Semantic(new Color("7a4bd6"));
        Color onDark = Colors.White;
        Color onLight = new Color("1b1d21");
        Color meterBg = bg.Lerp(dark ? Colors.Black : Colors.White, 0.22f);

        return new ToolPalette
        {
            Name = name,
            Dark = dark,
            Bg = bg, Panel = panel, Panel2 = panel2,
            PanelMaster = panel.Lerp(accent, 0.05f), PanelSnapshot = panel.Lerp(snapshot, 0.1f),
            Hover = Step(0.09f), Selection = bg.Lerp(accent, highContrast ? 0.45f : 0.28f), Border = border, Grid = Step(0.09f), GridStrong = Step(0.26f), MeterBg = meterBg,
            Fg = fg, Muted = muted, OnDark = onDark, OnLight = onLight,
            Accent = accent, AccentFg = OnFill(accent, onDark, onLight), AccentHover = accent.Lerp(far, 0.12f), AccentDim = accent.Lerp(bg, 0.55f),
            Danger = dangerC, DangerDim = dangerC.Lerp(bg, 0.7f), Warning = warningC, Success = successC, Info = infoC,
            MeterLow = successC, MeterMid = warningC, MeterHigh = dangerC, MeterHold = fg,
            Playhead = Semantic(new Color("ff4b4b")), Cursor = Semantic(new Color("9ad0ff")), SelectionOutline = far,
            Snapshot = snapshot, SnapshotFg = EnsureContrast(snapshot.Lerp(far, 0.4f), bg, 4.5f), Fader = fg.Lerp(muted, 0.4f),
            Mute = Semantic(new Color("c9a227")), Solo = Semantic(new Color("3fb950")), Bypass = Semantic(new Color("d26b2f")),
            TypeClip = Semantic(new Color("3d7bd9")), TypeMulti = Semantic(new Color("8a5bd6")), TypeScatterer = Semantic(new Color("3fa36a")), TypeSilence = Step(0.3f),
            TypeEvent = Semantic(new Color("e0853a")), TypeProgrammer = Semantic(new Color("2fa8a3")), TypeCommand = Semantic(new Color("c9b43a")), TypeSnapshot = Semantic(new Color("d65b9b")),
            TypeMarker = infoC, TypeTrack = muted, TypeOther = Step(0.4f),
            MarkerLoop = infoC, MarkerTransition = Semantic(new Color("ffb347")), MarkerSustain = Semantic(new Color("ff6b6b")), MarkerDestination = Semantic(new Color("7ee787")), MarkerMagnet = Semantic(new Color("d2a8ff")), MarkerOther = muted,
            BarPlaying = successC.Lerp(bg, 0.3f), BarStopping = warningC.Lerp(bg, 0.45f), BarVirtual = Step(0.2f),
        };
    }

    public static ToolPalette Derive(string name, Color bg, Color fg, Color accent, Color? danger = null, Color? warning = null, Color? success = null, Color? info = null, bool highContrast = false)
        => Derive(name, bg, fg, accent, out _, danger, warning, success, info, highContrast);
}

/// <summary>
/// The one palette every tool document and canvas follows. Selected by the <c>dirge/theme</c> editor setting; the
/// <see cref="ToolThemePreset.Editor"/> preset (default) is derived from the running editor theme, so it tracks light
/// editor themes and accent changes. <see cref="Changed"/> fires after the palette changed (editor settings, editor theme
/// or custom colors); the shell installs the watcher with <see cref="Watch"/>. Outside the editor it is Dirge Dark.
/// </summary>
public static partial class ToolTheme
{
    public const string Setting = "dirge/theme";
    public const string AccentSetting = "dirge/theme_accent";
    public const string BackgroundSetting = "dirge/theme_background";
    public const string ForegroundSetting = "dirge/theme_foreground";
    public const string PresetHint = "Editor,Dirge Dark,Dirge Light,High Contrast,Custom";

    public static readonly ToolThemePreset[] Presets = { ToolThemePreset.Editor, ToolThemePreset.DirgeDark, ToolThemePreset.DirgeLight, ToolThemePreset.HighContrast, ToolThemePreset.Custom };

    private static ToolPalette _current = ToolPalette.DirgeDark;
    private static bool _known;
    private static string? _warnedCustom;

    /// <summary>Current palette. Computed on first use.</summary>
    public static ToolPalette Current
    {
        get
        {
            if (!_known) Refresh();
            return _current;
        }
    }

    /// <summary>Raised after <see cref="Current"/> changed.</summary>
    public static event Action? Changed;

    /// <summary>The selected preset (<see cref="ToolThemePreset.Editor"/> outside the editor or when unset).</summary>
    public static ToolThemePreset Preset
    {
        get
        {
            if (ToolSettings.TryGet(Setting, out Variant value))
            {
                int index = value.AsInt32();
                if (index >= 0 && index < Presets.Length) return Presets[index];
            }
            return ToolThemePreset.Editor;
        }
    }

    /// <summary>Writes <c>dirge/theme</c> (the watcher then refreshes); no-op outside the editor.</summary>
    public static void SetPreset(ToolThemePreset preset) => ToolSettings.Set(Setting, (int)preset);

    /// <summary>Display name of <paramref name="preset"/> as listed in the setting's enum hint.</summary>
    public static string PresetName(ToolThemePreset preset) => preset switch
    {
        ToolThemePreset.Editor => "Editor",
        ToolThemePreset.DirgeDark => "Dirge Dark",
        ToolThemePreset.DirgeLight => "Dirge Light",
        ToolThemePreset.HighContrast => "High Contrast",
        _ => "Custom",
    };

    /// <summary>Recomputes <see cref="Current"/>; raises <see cref="Changed"/> when the palette differs. Returns the new palette.</summary>
    public static ToolPalette Refresh()
    {
        ToolPalette value = Compute();
        bool changed = _known && value != _current;
        _current = value;
        _known = true;
        if (changed) Changed?.Invoke();
        return value;
    }

    private static ToolPalette Compute()
    {
#if TOOLS
        if (Engine.IsEditorHint())
        {
            switch (Preset)
            {
                case ToolThemePreset.DirgeDark: return ToolPalette.DirgeDark;
                case ToolThemePreset.DirgeLight: return ToolPalette.DirgeLight;
                case ToolThemePreset.HighContrast: return ToolPalette.HighContrast;
                case ToolThemePreset.Custom: return CustomPalette();
                default: return EditorPalette() ?? ToolPalette.DirgeDark;
            }
        }
#endif
        return ToolPalette.DirgeDark;
    }

#if TOOLS
    private static ToolPalette CustomPalette()
    {
        Color bg = ToolSettings.TryGet(BackgroundSetting, out Variant b) ? b.AsColor() : ToolPalette.DirgeDark.Bg;
        Color fg = ToolSettings.TryGet(ForegroundSetting, out Variant f) ? f.AsColor() : ToolPalette.DirgeDark.Fg;
        Color accent = ToolSettings.TryGet(AccentSetting, out Variant a) ? a.AsColor() : ToolPalette.DirgeDark.Accent;
        ToolPalette palette = ToolPalette.Derive("Custom", bg, fg, accent, out bool adjusted);
        if (adjusted)
        {
            string key = bg.ToHtml() + fg.ToHtml() + accent.ToHtml();
            if (_warnedCustom != key)
            {
                _warnedCustom = key;
                GD.PushWarning($"Dirge: custom theme colors fail the contrast floor (text 4.5:1, accent 3:1 against the background); using {ToolPalette.Hex(palette.Fg)} on {ToolPalette.Hex(palette.Bg)} with accent {ToolPalette.Hex(palette.Accent)}.");
            }
        }
        return palette;
    }

    /// <summary>
    /// Palette from the editor theme: <c>Editor/dark_color_2</c> (window background), <c>dark_color_1</c> and <c>base_color</c>
    /// as surfaces, <c>font_color</c>, <c>accent_color</c> and the semantic colors. The editor's <c>dark_color_*</c> and font
    /// colors are translucent overlays in 4.7, so each is composited over the base before use. Falls back to the
    /// <c>interface/theme/*</c> settings when the theme lacks a color; null when neither is available.
    /// </summary>
    private static ToolPalette? EditorPalette()
    {
        Theme? theme = EditorInterface.Singleton.GetEditorTheme();
        EditorSettings? settings = EditorInterface.Singleton.GetEditorSettings();
        const string type = "Editor";
        Color? Themed(string name) => theme is not null && theme.HasColor(name, type) ? theme.GetColor(name, type) : null;
        Color? FromSettings(string name) => settings is not null && settings.HasSetting(name) ? settings.GetSetting(name).AsColor() : null;

        Color? baseColor = Themed("base_color") ?? FromSettings("interface/theme/base_color");
        Color? accent = Themed("accent_color") ?? FromSettings("interface/theme/accent_color");
        if (baseColor is null || accent is null) return null;
        Color @base = baseColor.Value with { A = 1f };
        Color bg = Themed("dark_color_2") is { } dark2 ? ToolPalette.Flatten(dark2, @base) : @base;
        bool dark = ToolPalette.IsDark(bg);
        Color fg = Themed("font_color") is { } font ? ToolPalette.Flatten(font, bg) : (dark ? new Color("e0e0e0") : new Color("1f2328"));
        Color? Semantic(string name) => Themed(name) is { } c ? ToolPalette.Flatten(c, bg) : null;
        ToolPalette palette = ToolPalette.Derive("Editor", bg, fg, ToolPalette.Flatten(accent.Value, bg),
            danger: Semantic("error_color"), warning: Semantic("warning_color"), success: Semantic("success_color"));
        Color? panel = Themed("dark_color_1") is { } dark1 ? ToolPalette.Flatten(dark1, @base) : null;
        if (panel is not null && ToolPalette.IsDark(panel.Value) == dark && ToolPalette.IsDark(@base) == dark)
        {
            // Keep the editor's own surface steps so Dirge panels line up with the docks around them.
            palette = palette with { Panel = panel.Value, Panel2 = @base, PanelMaster = panel.Value.Lerp(palette.Accent, 0.05f), PanelSnapshot = panel.Value.Lerp(palette.Snapshot, 0.1f) };
        }
        Color? highlight = Themed("highlight_color");
        if (highlight is not null) palette = palette with { Selection = ToolPalette.Flatten(highlight.Value, palette.Panel2) };
        return palette;
    }
#endif

    /// <summary>
    /// Registers the <c>dirge/theme*</c> editor settings and adds a child node to <paramref name="owner"/> that refreshes the
    /// palette on editor-settings changes (which include editor theme changes) for as long as the owner is in the tree.
    /// No-op outside the editor.
    /// </summary>
    public static void Watch(Node owner)
    {
#if TOOLS
        if (!Engine.IsEditorHint()) return;
        EditorSettings? settings = EditorInterface.Singleton.GetEditorSettings();
        if (settings is not null)
        {
            Register(settings, Setting, (int)ToolThemePreset.Editor, PropertyHint.Enum, PresetHint);
            Register(settings, AccentSetting, ToolPalette.DirgeDark.Accent, PropertyHint.ColorNoAlpha, "");
            Register(settings, BackgroundSetting, ToolPalette.DirgeDark.Bg, PropertyHint.ColorNoAlpha, "");
            Register(settings, ForegroundSetting, ToolPalette.DirgeDark.Fg, PropertyHint.ColorNoAlpha, "");
        }
        owner.AddChild(new Watcher { Name = "ToolThemeWatcher" });
        Refresh();
#endif
    }

#if TOOLS
    private static void Register(EditorSettings settings, string name, Variant initial, PropertyHint hint, string hintString)
    {
        if (!settings.HasSetting(name))
        {
            settings.SetSetting(name, initial);
            settings.SetInitialValue(name, initial, false);
        }
        settings.AddPropertyInfo(new Godot.Collections.Dictionary
        {
            ["name"] = name,
            ["type"] = (int)initial.VariantType,
            ["hint"] = (int)hint,
            ["hint_string"] = hintString,
        });
    }

    /// <summary>
    /// Forwards editor-settings changes to <see cref="Refresh"/>. Deferred so the editor has rebuilt its theme (which the
    /// Editor preset reads) before the palette is recomputed.
    /// </summary>
    private sealed partial class Watcher : Node
    {
        private EditorSettings? _settings;
        private bool _pending;

        public override void _EnterTree()
        {
            _settings = EditorInterface.Singleton.GetEditorSettings();
            if (_settings is not null) _settings.SettingsChanged += OnSettingsChanged;
        }

        public override void _ExitTree()
        {
            if (_settings is not null) _settings.SettingsChanged -= OnSettingsChanged;
            _settings = null;
        }

        private void OnSettingsChanged()
        {
            if (_pending) return;
            _pending = true;
            Callable.From(RefreshDeferred).CallDeferred();
        }

        private void RefreshDeferred()
        {
            _pending = false;
            Refresh();
        }
    }
#endif
}
