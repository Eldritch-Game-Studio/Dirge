@tool
extends Node
const Settings = preload("res://addons/dirge/tool/Core/tool_settings.gd")
const PRESETS = ["Editor", "Dirge Dark", "Dirge Light", "High Contrast", "Custom"]
const DARK = {
	"bg":"1b1d21", "panel":"24272c", "panel-2":"2c3036", "panel-master":"24262e", "panel-snapshot":"23202c", "hover":"30353c", "selection":"3b4b63", "border":"3a3f47", "grid":"2f3238", "grid-strong":"565b66", "meter-bg":"15171a",
	"fg":"d8dbe0", "muted":"8a9099", "on-dark":"ffffff", "on-light":"1b1d21", "accent":"e8a33d", "accent-fg":"1b1d21", "accent-hover":"f0b355", "accent-dim":"2d5f91", "danger":"e0584f", "danger-dim":"5a1f1f", "warning":"e3b341", "success":"4caf50", "info":"4fb0ff",
	"meter-low":"4caf50", "meter-mid":"e3b341", "meter-high":"e0584f", "meter-hold":"e6e8ef", "playhead":"ff4b4b", "cursor":"9ad0ff", "selection-outline":"ffffff", "snapshot":"7a4bd6", "snapshot-fg":"c9b3ff", "fader":"b8bcc8", "mute":"c9a227", "solo":"3fb950", "bypass":"d26b2f",
	"type-clip":"3d7bd9", "type-multi":"8a5bd6", "type-scatterer":"3fa36a", "type-silence":"5c6068", "type-event":"e0853a", "type-programmer":"2fa8a3", "type-command":"c9b43a", "type-snapshot":"d65b9b", "type-marker":"4fb0ff", "type-track":"8a9099", "type-other":"777777",
	"marker-loop":"4fb0ff", "marker-transition":"ffb347", "marker-sustain":"ff6b6b", "marker-destination":"7ee787", "marker-magnet":"d2a8ff", "marker-other":"aaaaaa", "bar-playing":"3f8f5a", "bar-stopping":"8f6a3f", "bar-virtual":"4a4d57"
}
signal changed
var current: Dictionary = {}
var _pending: bool = false
static var _cached: Dictionary = {}
static var _warning_key: String = ""

static func luminance(color: Color) -> float:
	var linear = color.srgb_to_linear()
	return 0.2126 * linear.r + 0.7152 * linear.g + 0.0722 * linear.b

static func contrast(a: Color, b: Color) -> float:
	var la = luminance(a) + 0.05
	var lb = luminance(b) + 0.05
	return maxf(la, lb) / minf(la, lb)

static func ensure_contrast(color: Color, against: Color, minimum: float) -> Color:
	if contrast(color, against) >= minimum:
		return color
	var target = Color.WHITE if luminance(against) < 0.2 else Color.BLACK
	for index in range(1, 33):
		var candidate = color.lerp(target, index / 32.0)
		if contrast(candidate, against) >= minimum:
			return candidate
	return target

static func flatten(color: Color, background: Color) -> Color:
	var opaque = Color(color, 1.0)
	return Color(background.lerp(opaque, color.a), 1.0)

static func on_fill(fill: Color, light: Color = Color.WHITE, dark: Color = Color("1b1d21")) -> Color:
	return dark if contrast(dark, fill) >= contrast(light, fill) else light

static func dark_palette() -> Dictionary:
	var result: Dictionary = {}
	for key in DARK:
		result[key] = Color(DARK[key])
	return result

static func derive(bg: Color, fg: Color, accent: Color, high_contrast: bool = false, semantics: Dictionary = {}) -> Dictionary:
	bg.a = 1.0
	fg = flatten(fg, bg)
	accent = flatten(accent, bg)
	var dark = luminance(bg) < 0.2
	var far = Color.WHITE if dark else Color.BLACK
	if contrast(fg, bg) < 4.5:
		fg = on_fill(bg, Color.WHITE, Color.BLACK)
	accent = ensure_contrast(accent, bg, 3.0)
	var result = dark_palette()
	var boost = 2.0 if high_contrast else 1.0
	var step = func(t: float) -> Color: return bg.lerp(far, clampf(t * boost, 0.0, 1.0))
	var semantic = func(color: Color) -> Color: return ensure_contrast(color, bg, 4.5 if high_contrast else 3.0)
	for key in result:
		if key.begins_with("type-") or key.begins_with("marker-") or key in ["danger", "warning", "success", "info", "snapshot", "mute", "solo", "bypass", "playhead", "cursor"]:
			result[key] = semantic.call(semantics.get(key, result[key]))
	result.bg = bg
	result.fg = fg
	result.accent = accent
	result.panel = step.call(0.04)
	result["panel-2"] = step.call(0.075)
	result["panel-master"] = result.panel.lerp(accent, 0.05)
	result["panel-snapshot"] = result.panel.lerp(result.snapshot, 0.1)
	result.hover = step.call(0.09)
	result.selection = bg.lerp(accent, 0.45 if high_contrast else 0.28)
	result.border = step.call(0.135)
	result.grid = step.call(0.09)
	result["grid-strong"] = step.call(0.26)
	result["meter-bg"] = bg.lerp(Color.BLACK if dark else Color.WHITE, 0.22)
	result.muted = ensure_contrast(bg.lerp(fg, 0.8 if high_contrast else 0.58), result["panel-2"], 7.0 if high_contrast else 3.5)
	result["accent-fg"] = on_fill(accent)
	result["accent-hover"] = accent.lerp(far, 0.12)
	result["accent-dim"] = accent.lerp(bg, 0.55)
	result["danger-dim"] = result.danger.lerp(bg, 0.7)
	result["meter-low"] = result.success
	result["meter-mid"] = result.warning
	result["meter-high"] = result.danger
	result["meter-hold"] = fg
	result["selection-outline"] = far
	result["snapshot-fg"] = ensure_contrast(result.snapshot.lerp(far, 0.4), bg, 4.5)
	result.fader = fg.lerp(result.muted, 0.4)
	result["type-silence"] = step.call(0.3)
	result["type-marker"] = result.info
	result["type-track"] = result.muted
	result["type-other"] = step.call(0.4)
	result["marker-other"] = result.muted
	result["bar-playing"] = result.success.lerp(bg, 0.3)
	result["bar-stopping"] = result.warning.lerp(bg, 0.45)
	result["bar-virtual"] = step.call(0.2)
	return result

static func preset() -> int:
	return clampi(int(Settings.get_value("dirge/theme", 0)), 0, PRESETS.size() - 1)

static func set_preset(index: int) -> void:
	Settings.set_value("dirge/theme", clampi(index, 0, PRESETS.size() - 1))
	_cached.clear()

static func palette() -> Dictionary:
	if _cached.is_empty():
		_cached = _compute()
	return _cached

static func css_text() -> String:
	var declarations: PackedStringArray = []
	var colors = palette()
	for key in colors:
		declarations.append("--%s: #%s" % [key, colors[key].to_html(false)])
	return "; ".join(declarations) + ";"

static func _compute() -> Dictionary:
	if not Engine.is_editor_hint() or preset() == 1:
		return dark_palette()
	match preset():
		2: return derive(Color("f3f4f6"), Color("1f2328"), Color("b85c00"))
		3: return derive(Color.BLACK, Color.WHITE, Color("ffd54a"), true)
		4:
			var bg: Color = Settings.get_value("dirge/theme_background", Color(DARK.bg))
			var fg: Color = Settings.get_value("dirge/theme_foreground", Color(DARK.fg))
			var accent: Color = Settings.get_value("dirge/theme_accent", Color(DARK.accent))
			var result = derive(bg, fg, accent)
			var key = bg.to_html() + fg.to_html() + accent.to_html()
			if (result.fg != fg or result.accent != accent) and key != _warning_key:
				_warning_key = key
				push_warning("Dirge: custom theme colors adjusted to text 4.5:1 and accent 3:1 contrast floors.")
			return result
	var editor_theme = EditorInterface.get_editor_theme()
	var base: Color = editor_theme.get_color("base_color", "Editor")
	base.a = 1.0
	var bg = flatten(editor_theme.get_color("dark_color_2", "Editor"), base)
	var fg = flatten(editor_theme.get_color("font_color", "Editor"), bg)
	var accent = flatten(editor_theme.get_color("accent_color", "Editor"), bg)
	var semantics: Dictionary = {}
	for pair in [["danger", "error_color"], ["warning", "warning_color"], ["success", "success_color"]]:
		if editor_theme.has_color(pair[1], "Editor"):
			semantics[pair[0]] = flatten(editor_theme.get_color(pair[1], "Editor"), bg)
	var result = derive(bg, fg, accent, false, semantics)
	var panel = flatten(editor_theme.get_color("dark_color_1", "Editor"), base)
	if (luminance(panel) < 0.2) == (luminance(bg) < 0.2) and (luminance(base) < 0.2) == (luminance(bg) < 0.2):
		result.panel = panel
		result["panel-2"] = base
		result["panel-master"] = panel.lerp(result.accent, 0.05)
		result["panel-snapshot"] = panel.lerp(result.snapshot, 0.1)
	if editor_theme.has_color("highlight_color", "Editor"):
		result.selection = flatten(editor_theme.get_color("highlight_color", "Editor"), result["panel-2"])
	return result

func _enter_tree() -> void:
	Settings.register("dirge/theme", 0, PROPERTY_HINT_ENUM, ",".join(PRESETS))
	for key in ["background", "foreground", "accent"]:
		var token = {"background": "bg", "foreground": "fg", "accent": "accent"}[key]
		Settings.register("dirge/theme_" + key, Color(DARK[token]), PROPERTY_HINT_COLOR_NO_ALPHA)
	if Engine.is_editor_hint():
		EditorInterface.get_editor_settings().settings_changed.connect(_settings_changed)
	refresh()

func _exit_tree() -> void:
	if Engine.is_editor_hint() and EditorInterface.get_editor_settings().settings_changed.is_connected(_settings_changed):
		EditorInterface.get_editor_settings().settings_changed.disconnect(_settings_changed)

func _settings_changed() -> void:
	if not _pending:
		_pending = true
		refresh.call_deferred()

func refresh() -> void:
	_pending = false
	_cached = _compute()
	if current != _cached:
		current = _cached
		changed.emit()
