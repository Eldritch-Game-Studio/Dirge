@tool
extends Control
signal bar_clicked(event_path: String, handle: int)
var bars: Array = []
var length: float = 0.0
var selected_handle: int = -1
var divergence_time: float = NAN
var pixels_per_second: float = 80.0
var scroll_time: float = 0.0
var scroll_y: float = 0.0
var ui_scale: float = 1.0
var title: String = "No recording"
var _row_count: int = 0
var _dragging: bool = false
var _drag_moved: bool = false
var _drag_origin = Vector2.ZERO
var _drag_scroll = Vector2.ZERO

func _init() -> void:
	custom_minimum_size = Vector2(240, 100)
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	clip_contents = true
	focus_mode = Control.FOCUS_CLICK

func set_recording(recording: Dictionary, recording_title: String = "Recording") -> void:
	bars.clear()
	length = 0.0
	_row_count = 0
	selected_handle = -1
	divergence_time = NAN
	scroll_y = 0.0
	title = recording_title
	var open = {}
	var events = {}
	for tick in recording.get("ticks", []):
		var time = float(tick.get("time", 0))
		length = time + float(tick.get("dt", 0))
		for call in tick.get("calls", []):
			var handle = int(call.get("handle", 0))
			match call.get("method", ""):
				"CreateInstance":
					events[int(call.get("result", 0))] = call.get("name", "?")
				"Start":
					if open.has(handle):
						bars[open[handle]].end = time
						bars[open[handle]].openEnd = false
					bars.append({"handle": handle, "event": events.get(handle, "#" + str(handle)), "start": time, "end": time, "openEnd": true, "stopAt": NAN, "row": 0})
					open[handle] = bars.size() - 1
				"Stop", "StopAll":
					if call.get("mode", 0) not in [0, "AllowFadeout"]: continue
					var handles = open.keys() if call.method == "StopAll" else [handle]
					for stopped in handles:
						if open.has(stopped) and is_nan(bars[open[stopped]].stopAt):
							bars[open[stopped]].stopAt = time
		for ended in tick.get("ended", []):
			var handle = int(ended)
			if open.has(handle):
				bars[open[handle]].end = length
				bars[open[handle]].openEnd = false
				open.erase(handle)
	for index in open.values():
		bars[index].end = length
	bars.sort_custom(func(a, b): return a.start < b.start if a.start != b.start else a.handle < b.handle)
	var rows = {}
	for bar in bars:
		if not rows.has(bar.handle): rows[bar.handle] = rows.size()
		bar.row = rows[bar.handle]
	_row_count = rows.size()
	fit_all()

func set_divergence(time: float) -> void:
	divergence_time = time
	queue_redraw()

func fit_all() -> void:
	pixels_per_second = clampf(maxf(1, size.x - 48 * ui_scale) / length, 4, 4000) if length > 0 else 80.0
	scroll_time = 0.0
	queue_redraw()

func select_handle(handle: int) -> void:
	selected_handle = handle
	queue_redraw()

func time_to_x(time: float) -> float:
	return 40 * ui_scale + (time - scroll_time) * pixels_per_second

func x_to_time(x: float) -> float:
	return scroll_time + (x - 40 * ui_scale) / pixels_per_second

func row_to_y(row: int) -> float:
	return (24 + row * 22) * ui_scale - scroll_y

func hit_test(position: Vector2) -> int:
	if position.y < 20 * ui_scale: return -1
	for index in range(bars.size()):
		var bar = bars[index]
		var rect = Rect2(time_to_x(bar.start), row_to_y(bar.row), maxf(3, (bar.end - bar.start) * pixels_per_second), 20 * ui_scale)
		if rect.has_point(position): return index
	return -1

func _clamp_scroll() -> void:
	var visible_time = maxf(1, size.x - 40 * ui_scale) / pixels_per_second
	scroll_time = clampf(scroll_time, -visible_time * 0.1, maxf(0, length - visible_time * 0.5))
	scroll_y = clampf(scroll_y, 0, maxf(0, (_row_count * 22 + 28) * ui_scale - size.y))

func _gui_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		if event.button_index in [MOUSE_BUTTON_WHEEL_UP, MOUSE_BUTTON_WHEEL_DOWN] and event.pressed:
			var up = event.button_index == MOUSE_BUTTON_WHEEL_UP
			if event.shift_pressed:
				scroll_y += (-20 if up else 20) * ui_scale
			else:
				var anchor = x_to_time(event.position.x)
				pixels_per_second = clampf(pixels_per_second * (1.25 if up else 0.8), 4, 4000)
				scroll_time = anchor - (event.position.x - 40 * ui_scale) / pixels_per_second
			_clamp_scroll()
			queue_redraw()
			accept_event()
		elif event.button_index in [MOUSE_BUTTON_LEFT, MOUSE_BUTTON_MIDDLE]:
			if event.pressed:
				grab_focus()
				_dragging = true
				_drag_moved = false
				_drag_origin = event.position
				_drag_scroll = Vector2(scroll_time, scroll_y)
			elif _dragging:
				_dragging = false
				if not _drag_moved and event.button_index == MOUSE_BUTTON_LEFT:
					var hit = hit_test(event.position)
					if hit >= 0:
						select_handle(bars[hit].handle)
						bar_clicked.emit(str(bars[hit].event), bars[hit].handle)
			accept_event()
	elif event is InputEventMouseMotion and _dragging:
		var delta = event.position - _drag_origin
		_drag_moved = _drag_moved or delta.length_squared() > 9
		if _drag_moved:
			scroll_time = _drag_scroll.x - delta.x / pixels_per_second
			scroll_y = _drag_scroll.y - delta.y
			_clamp_scroll()
			queue_redraw()
		accept_event()

func _draw() -> void:
	var palette = preload("res://addons/dirge/tool/Core/tool_theme.gd").palette()
	ui_scale = preload("res://addons/dirge/tool/Core/tool_scale.gd").compute()
	var fg: Color = palette.get("fg", get_theme_color("font_color", "Label"))
	var bg: Color = palette.get("bg", Color.BLACK)
	var panel: Color = palette.get("panel", bg.lightened(0.1))
	var muted: Color = palette.get("muted", fg.darkened(0.3))
	var grid: Color = palette.get("grid", muted * Color(1, 1, 1, 0.25))
	var playing: Color = palette.get("bar-playing", palette.get("accent", fg))
	var stopping: Color = palette.get("bar-stopping", palette.get("warning", fg))
	var font = get_theme_default_font()
	var font_size = maxi(8, roundi(11 * ui_scale))
	draw_rect(Rect2(Vector2.ZERO, size), bg)
	if bars.is_empty():
		draw_string(font, Vector2(8, font_size + 4), title, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, muted)
		draw_string(font, Vector2(8, font_size * 2 + 10), "Record or load a recording to see event lifetimes.", HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, muted)
		return
	for bar in bars:
		var y = row_to_y(bar.row)
		var x0 = time_to_x(bar.start)
		var x1 = maxf(time_to_x(bar.end), x0 + 3)
		if y + 20 * ui_scale < 20 * ui_scale or y > size.y or x1 < 40 * ui_scale or x0 > size.x: continue
		var left = maxf(x0, 40 * ui_scale)
		var right = minf(x1, size.x)
		var rect = Rect2(left, y, right - left, 20 * ui_scale)
		draw_rect(rect, playing)
		if not is_nan(bar.stopAt):
			var stop = clampf(time_to_x(bar.stopAt), left, right)
			draw_rect(Rect2(stop, y, right - stop, 20 * ui_scale), stopping)
		if bar.handle == selected_handle: draw_rect(rect, fg, false, 2)
		if bar.openEnd and x1 < size.x:
			for dash in range(3): draw_rect(Rect2(x1 + 2 + dash * 5, y + 8 * ui_scale, 3, 4 * ui_scale), muted)
		if right - left > 24:
			draw_string(font, Vector2(left + 4, y + 10 * ui_scale + font_size * 0.35), str(bar.event), HORIZONTAL_ALIGNMENT_LEFT, right - left - 6, font_size, fg)
	draw_rect(Rect2(0, 20 * ui_scale, 40 * ui_scale, maxf(0, size.y - 20 * ui_scale)), panel)
	var shown = {}
	for bar in bars:
		if shown.has(bar.handle): continue
		shown[bar.handle] = true
		draw_string(font, Vector2(4, row_to_y(bar.row) + 14 * ui_scale), "#%d" % bar.handle, HORIZONTAL_ALIGNMENT_LEFT, 34 * ui_scale, font_size, muted)
	draw_rect(Rect2(0, 0, size.x, 20 * ui_scale), panel)
	var step = preload("res://addons/dirge/tool/Profiler/update_graph.gd").nice_ceiling(80 / pixels_per_second)
	var time = floor(maxf(0, scroll_time) / step) * step
	while time_to_x(time) < size.x:
		var x = time_to_x(time)
		if x >= 40 * ui_scale:
			draw_line(Vector2(x, 20 * ui_scale), Vector2(x, size.y), grid)
			draw_string(font, Vector2(x + 3, font_size + 1), String.num(time, 3) + " s", HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, muted)
		time += step
	draw_rect(Rect2(0, size.y - 18 * ui_scale, size.x, 18 * ui_scale), panel)
	draw_string(font, Vector2(6, size.y - 4), title, HORIZONTAL_ALIGNMENT_LEFT, size.x * 0.6, font_size, fg)
	draw_string(font, Vector2(size.x * 0.6, size.y - 4), "wheel zoom · shift+wheel rows · drag pan", HORIZONTAL_ALIGNMENT_RIGHT, size.x * 0.4 - 6, font_size, muted)
	var end_x = time_to_x(length)
	if end_x >= 40 * ui_scale and end_x <= size.x: draw_line(Vector2(end_x, 20 * ui_scale), Vector2(end_x, size.y - 18 * ui_scale), muted, 2)
	if not is_nan(divergence_time):
		var x = time_to_x(divergence_time)
		if x >= 40 * ui_scale and x <= size.x: draw_line(Vector2(x, 0), Vector2(x, size.y - 18 * ui_scale), palette.get("playhead", stopping), 2)
