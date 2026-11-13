@tool
extends Control
## The original profiler canvas: fixed-capacity 10-second update/voice history.
const CAPACITY = 4096
const WINDOW_SECONDS = 10.0
var voice_capacity: int = 64
var ui_scale: float = 1.0
var sample_count: int = 0
var _head: int = 0
var _time = PackedFloat64Array()
var _ms = PackedFloat32Array()
var _voices = PackedInt32Array()
var _now: float = 0.0
var _drawn_at: float = -INF
var _last_ms: float = NAN
var _last_voices: int = -1

func _init() -> void:
	_time.resize(CAPACITY)
	_ms.resize(CAPACITY)
	_voices.resize(CAPACITY)
	custom_minimum_size = Vector2(240, 140)
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	clip_contents = true

func push(wall_seconds: float, update_ms: float, real_voices: int) -> void:
	var index = (_head + sample_count) % CAPACITY
	if sample_count == CAPACITY:
		_head = (_head + 1) % CAPACITY
	else:
		sample_count += 1
	_time[index] = wall_seconds
	_ms[index] = update_ms
	_voices[index] = real_voices
	_now = wall_seconds
	_last_ms = update_ms
	_last_voices = real_voices

func advance(wall_seconds: float) -> void:
	if wall_seconds - _drawn_at < 1.0 / 30.0 - 0.002:
		return
	_drawn_at = wall_seconds
	_now = wall_seconds
	queue_redraw()

func clear() -> void:
	_head = 0
	sample_count = 0
	_last_ms = NAN
	_last_voices = -1
	queue_redraw()

static func nice_ceiling(value: float) -> float:
	if value <= 0.0:
		return 1.0
	var magnitude = pow(10.0, floor(log(value) / log(10.0)))
	var normalized = value / magnitude
	return (1.0 if normalized <= 1.0 else 2.0 if normalized <= 2.0 else 5.0 if normalized <= 5.0 else 10.0) * magnitude

func scales() -> Vector2:
	var max_ms = 0.0
	var max_voices = maxi(8, voice_capacity)
	for i in range(sample_count):
		var index = (_head + i) % CAPACITY
		if _time[index] < _now - WINDOW_SECONDS:
			continue
		max_ms = maxf(max_ms, _ms[index])
		max_voices = maxi(max_voices, _voices[index])
	return Vector2(nice_ceiling(maxf(max_ms * 1.15, 0.5)), max_voices if max_voices <= maxi(8, voice_capacity) else nice_ceiling(max_voices))

func _draw() -> void:
	var palette = preload("res://addons/dirge/tool/Core/tool_theme.gd").palette()
	ui_scale = preload("res://addons/dirge/tool/Core/tool_scale.gd").compute()
	var fg: Color = palette.get("fg", get_theme_color("font_color", "Label"))
	var accent: Color = palette.get("accent", fg)
	var series: Color = palette.get("info", fg.darkened(0.25))
	var background: Color = palette.get("bg", Color.BLACK)
	var panel: Color = palette.get("panel", background.lightened(0.1))
	var grid: Color = palette.get("grid", fg * Color(1, 1, 1, 0.15))
	draw_rect(Rect2(Vector2.ZERO, size), background)
	var plot = Rect2(Vector2(44, 18) * ui_scale, Vector2(maxf(1, size.x - 84 * ui_scale), maxf(1, size.y - 34 * ui_scale)))
	draw_rect(plot, panel)
	var axes = scales()
	for i in range(5):
		var y = plot.end.y - plot.size.y * i / 4.0
		draw_line(Vector2(plot.position.x, y), Vector2(plot.end.x, y), grid)
	for second in range(11):
		var time = floor(_now - WINDOW_SECONDS + second)
		var x = plot.position.x + (time - (_now - WINDOW_SECONDS)) / WINDOW_SECONDS * plot.size.x
		if x >= plot.position.x and x <= plot.end.x:
			draw_line(Vector2(x, plot.position.y), Vector2(x, plot.end.y), grid)
	var previous_ms = Vector2.ZERO
	var previous_voice = Vector2.ZERO
	var has_previous = false
	for i in range(sample_count):
		var index = (_head + i) % CAPACITY
		if _time[index] < _now - WINDOW_SECONDS: continue
		var x = plot.position.x + (_time[index] - (_now - WINDOW_SECONDS)) / WINDOW_SECONDS * plot.size.x
		var point_ms = Vector2(x, plot.end.y - clampf(_ms[index] / axes.x, 0, 1) * plot.size.y)
		var point_voice = Vector2(x, plot.end.y - clampf(_voices[index] / axes.y, 0, 1) * plot.size.y)
		if has_previous:
			draw_line(previous_ms, point_ms, accent, 1.5)
			draw_line(previous_voice, Vector2(x, previous_voice.y), series, 1.5)
			draw_line(Vector2(x, previous_voice.y), point_voice, series, 1.5)
		previous_ms = point_ms
		previous_voice = point_voice
		has_previous = true
	var font = get_theme_default_font()
	var font_size = maxi(8, roundi(11 * ui_scale))
	for fraction in [0.0, 0.5, 1.0]:
		var y = plot.end.y - fraction * plot.size.y + font_size * 0.35
		draw_string(font, Vector2(2, y), String.num(fraction * axes.x, 3), HORIZONTAL_ALIGNMENT_LEFT, 40 * ui_scale, font_size, accent)
		draw_string(font, Vector2(plot.end.x + 4, y), String.num(fraction * axes.y, 3), HORIZONTAL_ALIGNMENT_LEFT, 36 * ui_scale, font_size, series)
	draw_string(font, Vector2(plot.position.x, font_size), "— ms" if is_nan(_last_ms) else "%.2f ms" % _last_ms, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, accent)
	draw_string(font, Vector2(plot.position.x + 110 * ui_scale, font_size), "— voices" if _last_voices < 0 else "%d voices" % _last_voices, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, series)
	draw_string(font, Vector2(plot.position.x, size.y - 3), "-10 s", HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, fg)
	draw_string(font, Vector2(plot.end.x - 26 * ui_scale, size.y - 3), "now", HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, fg)
