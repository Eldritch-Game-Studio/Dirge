@tool
extends Control

const Edits = preload("res://addons/dirge/tool/Events/event_edits.gd")
const Palette = preload("res://addons/dirge/tool/Core/tool_theme.gd")
const NONE = "00000000000000000000000000000000"
signal copy_requested
signal paste_requested
signal duplicate_requested
signal delete_requested
var state
var edits
var lengths
var audition
var zoom: float = 120.0
var scroll_x: float = 0.0
var scroll_y: float = 0.0
var last_pointer_track: Dictionary = {}
var last_pointer_unit: float = 0.0
var ui_scale: float = 1.0
var blocks: Array = []
var markers: Array = []
var rows: Array = []
var tabs: Array = []
var tempos: Array = []
var _view_sheet: String = NONE
var _timeline_view = Vector2(120, 0)
var _layout_key: Array = []
var _mode: String = ""
var _press: Vector2
var _pressed: String = NONE
var _original: Array = []
var _preview: Array = []
var _band: Rect2
var _band_additive: bool = false
var _tempo: Dictionary = {}
var _tempo_position: float = 0.0
var _menu: PopupMenu
var _clock: float = 0.0
var _live_value: float = NAN

func setup(editor_state, event_edits, asset_lengths, server = null) -> void:
	state = editor_state
	edits = event_edits
	lengths = asset_lengths
	audition = server
	clip_contents = true
	focus_mode = Control.FOCUS_ALL
	mouse_filter = Control.MOUSE_FILTER_STOP
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	custom_minimum_size = Vector2(320, 160)
	state.selection_changed.connect(invalidate)
	resized.connect(invalidate)

func _ready() -> void:
	_menu = PopupMenu.new()
	add_child(_menu)
	_menu.id_pressed.connect(_on_menu)

func invalidate() -> void:
	_layout_key.clear()
	queue_redraw()

func header_width() -> float: return 150.0 * ui_scale
func track_height() -> float: return 64.0 * ui_scale
func marker_top() -> float:
	return (64.0 if has_tempo() else 50.0) * ui_scale
func tracks_top() -> float:
	return marker_top() + (22.0 * ui_scale if state.sheet == NONE else 0.0)
func has_tempo() -> bool:
	return state != null and state.sheet == NONE and not state.event.get("tempo", []).is_empty()
func x_of(unit: float) -> float: return header_width() + (unit - scroll_x) * zoom
func unit_of(x: float) -> float: return (x - header_width()) / zoom + scroll_x
func visible_units() -> float: return maxf(0.001, (size.x - header_width()) / zoom)

func snap(unit: float) -> float:
	if not state.snap_to_grid: return unit
	if state.sheet != NONE:
		var parameter = state.find_parameter(state.sheet)
		return snappedf(unit, 0.1 if parameter.get("kind", "Continuous") == "Continuous" else 1.0)
	var tempo: Array = state.event.get("tempo", [])
	if tempo.is_empty(): return snappedf(unit, 0.1)
	var segment = 0
	for index in tempo.size():
		if float(tempo[index].position) <= unit: segment = index
	var marker: Dictionary = tempo[segment]
	var result = float(marker.position) + snappedf(unit - float(marker.position), 60.0 / maxf(1.0, marker.bpm))
	if segment + 1 < tempo.size(): result = minf(result, tempo[segment + 1].position)
	return result

func clamp_unit(unit: float) -> float:
	if state.sheet == NONE: return maxf(0.0, unit)
	var parameter = state.find_parameter(state.sheet)
	return clampf(unit, parameter.get("minimum", 0.0), parameter.get("maximum", 1.0))

func default_sheet_length() -> float:
	var parameter = state.find_parameter(state.sheet)
	return (float(parameter.get("maximum", 1.0)) - float(parameter.get("minimum", 0.0))) * 0.25 if parameter.get("kind", "Continuous") == "Continuous" else 1.0

func ensure_layout() -> void:
	if state == null: return
	if _view_sheet != state.sheet:
		if _view_sheet == NONE: _timeline_view = Vector2(zoom, scroll_x)
		_view_sheet = state.sheet
		if _view_sheet == NONE:
			zoom = _timeline_view.x
			scroll_x = _timeline_view.y
		else: zoom_to_fit()
		scroll_y = 0.0
	var key = [state.revision, size, zoom, scroll_x, scroll_y, ui_scale]
	if key == _layout_key: return
	_layout_key = key
	blocks.clear()
	markers.clear()
	rows.clear()
	tabs.clear()
	tempos.clear()
	var event: Dictionary = state.event
	if event.is_empty(): return
	var x = 0.0
	var definitions = [{"id": NONE, "name": "Timeline"}] + state.parameters()
	for definition in definitions:
		var label: String = definition.get("name", "")
		if state.is_global_parameter(definition.id): label = "G: " + label
		var width = get_theme_default_font().get_string_size(label, HORIZONTAL_ALIGNMENT_LEFT, -1, int(11 * ui_scale)).x + 20 * ui_scale
		tabs.append({"rect": Rect2(x, 0, width, 24 * ui_scale), "label": label, "id": definition.id})
		x += width + 2 * ui_scale
	var y = tracks_top() - scroll_y
	for track in event.get("tracks", []):
		rows.append({"rect": Rect2(0, y, size.x, track_height()), "track": track})
		for instrument in track.get("instruments", []):
			var placement: Dictionary = instrument.placement
			if placement.get("parameter", NONE) != state.sheet: continue
			var duration = float(placement.get("length", 0.0))
			if duration <= 0: duration = lengths.natural_length(instrument) if state.sheet == NONE else 0.0
			blocks.append({"rect": Rect2(x_of(placement.get("start", 0.0)), y + 4 * ui_scale, maxf(8 * ui_scale, duration * zoom), track_height() - 8 * ui_scale), "instrument": instrument, "track": track})
		y += track_height()
	if state.sheet != NONE: return
	for marker in event.get("markers", []):
		var region = Edits.is_region(marker)
		markers.append({"rect": Rect2(x_of(marker.get("position", 0.0)), marker_top(), maxf(8 * ui_scale, float(marker.get("length", 0.0)) * zoom) if region else 40 * ui_scale, 22 * ui_scale), "marker": marker})
	for tempo in event.get("tempo", []):
		tempos.append({"rect": Rect2(x_of(tempo.position) + 16 * ui_scale, 50 * ui_scale, 90 * ui_scale, 14 * ui_scale), "tempo": tempo})

func _text(position: Vector2, text: String, color: Color, width: float = -1.0, font_size: int = 11) -> void:
	draw_string(get_theme_default_font(), position, text, HORIZONTAL_ALIGNMENT_LEFT, width, int(font_size * ui_scale), color)

func _preview_for(id: String) -> Dictionary:
	for item in _preview:
		if item.id == id: return item
	return {}

func _draw() -> void:
	if state == null: return
	ensure_layout()
	var palette = Palette.palette()
	var bg: Color = palette.bg
	var panel: Color = palette.panel
	var fg: Color = palette.fg
	var muted: Color = palette.muted
	var accent: Color = palette.accent
	var grid: Color = palette.grid
	draw_rect(Rect2(Vector2.ZERO, size), bg)
	if state.event.is_empty():
		_text(Vector2(12, size.y / 2), "Select an event in the browser to edit it.", muted)
		return
	for index in rows.size():
		var row: Dictionary = rows[index]
		if index % 2: draw_rect(row.rect, panel.darkened(0.25))
		draw_line(Vector2(0, row.rect.end.y), Vector2(size.x, row.rect.end.y), grid)
	var step = nice_step(visible_units(), maxf(1.0, (size.x - header_width()) / (70 * ui_scale)))
	var first = floorf(scroll_x / step) * step
	for index in mini(512, int(visible_units() / step) + 3):
		var value = first + index * step
		var px = x_of(value)
		if px < header_width(): continue
		draw_line(Vector2(px, tracks_top()), Vector2(px, size.y), grid)
	for block in blocks:
		var instrument: Dictionary = block.instrument
		var rect: Rect2 = block.rect
		var preview = _preview_for(instrument.id)
		if not preview.is_empty() and preview.has("start"):
			rect.position.x = x_of(preview.start)
			var duration = float(preview.length)
			if duration <= 0: duration = lengths.natural_length(instrument) if state.sheet == NONE else 0.0
			rect.size.x = maxf(8 * ui_scale, duration * zoom)
		if rect.end.x < header_width() or rect.end.y < tracks_top(): continue
		var fill: Color = palette.get("type-" + str(instrument.get("$type", "")).to_lower(), palette["type-other"])
		draw_rect(rect, fill)
		draw_rect(Rect2(rect.position, Vector2(rect.size.x, 3 * ui_scale)), fill.lightened(0.3))
		if instrument.id in state.selected_instruments: draw_rect(rect.grow(1), accent, false, 2)
		if rect.size.x > 24 * ui_scale:
			_text(rect.position + Vector2(5, 16) * ui_scale, instrument.get("name", instrument.get("$type", "")), Palette.on_fill(fill), rect.size.x - 8 * ui_scale)
			_text(Vector2(rect.position.x + 5 * ui_scale, rect.end.y - 6 * ui_scale), sub_label(instrument), Palette.on_fill(fill), rect.size.x - 8 * ui_scale, 10)
	# Paint the fixed header after scrolling content, masking offscreen blocks.
	draw_rect(Rect2(0, tracks_top(), header_width(), maxf(0, size.y - tracks_top())), panel)
	for row in rows:
		if row.rect.end.y <= tracks_top(): continue
		if row.track.id == state.selected_track: draw_rect(Rect2(0, row.rect.position.y, header_width(), track_height()), panel.lightened(0.15))
		_text(Vector2(8 * ui_scale, row.rect.position.y + 20 * ui_scale), row.track.get("name", "Track"), fg, header_width() - 16 * ui_scale)
		_text(Vector2(8 * ui_scale, row.rect.end.y - 8 * ui_scale), "%+.1f dB" % row.track.get("volumeDb", 0.0), muted)
	draw_rect(Rect2(0, 24 * ui_scale, size.x, tracks_top() - 24 * ui_scale), panel)
	for index in mini(512, int(visible_units() / step) + 3):
		var value = first + index * step
		var px = x_of(value)
		if px < header_width(): continue
		_text(Vector2(px + 3, 42 * ui_scale), format_unit(value), fg)
	if has_tempo(): _draw_beats(grid, muted)
	for item in markers:
		var marker: Dictionary = item.marker
		var rect: Rect2 = item.rect
		var preview = _preview_for(marker.id)
		if not preview.is_empty():
			rect.position.x = x_of(preview.position)
			if Edits.is_region(marker): rect.size.x = maxf(8 * ui_scale, preview.length * zoom)
		var token = {"Loop": "loop", "Transition": "transition", "TransitionRegion": "transition", "SustainPoint": "sustain", "Destination": "destination", "Magnet": "magnet"}.get(marker.get("$type", ""), "other")
		var color: Color = palette["marker-" + token]
		if Edits.is_region(marker):
			draw_rect(rect, color, false, 2)
			var tint = color
			tint.a = 0.08
			draw_rect(Rect2(rect.position.x, tracks_top(), rect.size.x, maxf(0, size.y - tracks_top())), tint)
		else:
			draw_rect(rect, color)
			draw_line(Vector2(rect.position.x, rect.end.y), Vector2(rect.position.x, size.y), color.darkened(0.4))
		_text(rect.position + Vector2(4, 15) * ui_scale, marker.get("name", "Marker"), bg if not Edits.is_region(marker) else color, rect.size.x - 6)
		if marker.id in state.selected_markers: draw_rect(rect.grow(1), accent, false, 2)
	for item in tempos:
		var rect: Rect2 = item.rect
		if item.tempo == _tempo and _mode == "tempo": rect.position.x = x_of(_tempo_position) + 16 * ui_scale
		draw_rect(rect, grid)
		_text(rect.position + Vector2(2, 11) * ui_scale, "%s bpm %d/4" % [String.num(item.tempo.bpm), item.tempo.beatsPerBar], fg, -1, 9)
	draw_rect(Rect2(0, 24 * ui_scale, header_width(), tracks_top() - 24 * ui_scale), panel)
	_text(Vector2(8, 42) * ui_scale, "Timeline" if state.sheet == NONE else state.find_parameter(state.sheet).get("name", "Parameter"), muted, header_width() - 45 * ui_scale)
	if state.snap_to_grid: _text(Vector2(header_width() - 38 * ui_scale, 42 * ui_scale), "snap", accent, -1, 9)
	draw_rect(Rect2(0, 0, size.x, 24 * ui_scale), panel)
	for tab in tabs:
		_text(tab.rect.position + Vector2(10, 16) * ui_scale, tab.label, fg if tab.id == state.sheet else muted)
		if tab.id == state.sheet: draw_line(Vector2(tab.rect.position.x, 23 * ui_scale), Vector2(tab.rect.end.x, 23 * ui_scale), accent, 2)
	var cursor_x = x_of(state.cursor)
	if cursor_x >= header_width(): draw_line(Vector2(cursor_x, 38 * ui_scale), Vector2(cursor_x, size.y), palette.cursor)
	if not is_nan(_live_value):
		var live_x = x_of(_live_value)
		if live_x >= header_width(): draw_line(Vector2(live_x, 24 * ui_scale), Vector2(live_x, size.y), palette.playhead, 2)
	if _mode == "band":
		var band_color: Color = palette.cursor
		band_color.a = 0.2
		draw_rect(_band.abs(), band_color)

func _draw_beats(grid: Color, text_color: Color) -> void:
	var tempo: Array = state.event.tempo
	var bar_number = 1
	var remaining = 2048
	for index in tempo.size():
		var marker: Dictionary = tempo[index]
		var beat = 60.0 / maxf(1, marker.bpm)
		var end = float(tempo[index + 1].position) if index + 1 < tempo.size() else scroll_x + visible_units()
		var beats_per_bar = maxi(1, marker.beatsPerBar)
		var start = maxi(0, int(floorf((scroll_x - float(marker.position)) / beat)))
		if beat * zoom >= 3:
			for number in range(start, maxi(start, int(ceilf((minf(end, scroll_x + visible_units()) - float(marker.position)) / beat)))):
				if remaining <= 0: break
				remaining -= 1
				var px = x_of(float(marker.position) + number * beat)
				if px < header_width(): continue
				var is_bar = number % beats_per_bar == 0
				draw_line(Vector2(px, 50 * ui_scale), Vector2(px, 64 * ui_scale), grid)
				if is_bar:
					_text(Vector2(px + 2, 61 * ui_scale), str(bar_number + number / beats_per_bar), text_color, -1, 9)
					draw_line(Vector2(px, tracks_top()), Vector2(px, size.y), grid)
		bar_number += int(ceilf((end - float(marker.position)) / (beat * beats_per_bar)))

static func nice_step(visible: float, max_ticks: float) -> float:
	var raw = visible / maxf(1, max_ticks)
	var magnitude = pow(10.0, floorf(log(maxf(raw, 0.000000001)) / log(10.0)))
	var normalized = raw / magnitude
	return magnitude * (1 if normalized <= 1 else 2 if normalized <= 2 else 5 if normalized <= 5 else 10)

func format_unit(value: float) -> String:
	if state.sheet == NONE:
		return "%d:%05.2f" % [int(value / 60), fmod(value, 60)] if value >= 60 else String.num(value) + " s"
	var parameter = state.find_parameter(state.sheet)
	var labels: Array = parameter.get("labels", [])
	var index = int(roundf(value))
	if parameter.get("kind", "Continuous") == "Labeled" and is_equal_approx(value, index) and index >= 0 and index < labels.size(): return labels[index]
	return String.num(value)

static func sub_label(instrument: Dictionary) -> String:
	match instrument.get("$type", ""):
		"Clip": return str(instrument.get("asset", "(no asset)")).get_file()
		"Multi", "Scatterer": return "%d entries" % instrument.get("entries", []).size()
		"Programmer": return "key: " + instrument.get("key", "")
		"Command": return instrument.get("action", "StartEvent")
	return instrument.get("$type", "")

func _hit(items: Array, position: Vector2) -> Dictionary:
	for index in range(items.size() - 1, -1, -1):
		if items[index].rect.has_point(position): return items[index]
	return {}

func _row(position: Vector2) -> Dictionary:
	if position.y < tracks_top(): return {}
	return _hit(rows, position).get("track", {})

func _gui_input(input: InputEvent) -> void:
	if state == null or state.event.is_empty(): return
	ensure_layout()
	if input is InputEventKey and input.pressed:
		if input.keycode == KEY_DELETE: delete_requested.emit(); accept_event()
		return
	if input is InputEventMouseButton:
		if input.button_index in [MOUSE_BUTTON_WHEEL_UP, MOUSE_BUTTON_WHEEL_DOWN] and input.pressed:
			var direction = 1.0 if input.button_index == MOUSE_BUTTON_WHEEL_UP else -1.0
			if input.ctrl_pressed: zoom_at(input.position.x, 1.15 if direction > 0 else 1.0 / 1.15)
			elif input.shift_pressed:
				scroll_y = clampf(scroll_y - direction * track_height() * 0.5, 0, maxf(0, rows.size() * track_height() - size.y + tracks_top()))
			else: scroll_x += -direction * visible_units() * 0.1 * input.factor; clamp_scroll()
			invalidate()
		elif input.button_index == MOUSE_BUTTON_RIGHT and input.pressed: open_menu(input.position)
		elif input.button_index == MOUSE_BUTTON_LEFT:
			if input.pressed: _left_press(input)
			else: _left_release(input.position)
		accept_event()
	elif input is InputEventMouseMotion:
		_motion(input.position)
		if not _mode.is_empty(): accept_event()

func _left_press(input: InputEventMouseButton) -> void:
	grab_focus()
	_press = input.position
	last_pointer_unit = snap(clamp_unit(unit_of(_press.x)))
	last_pointer_track = _row(_press)
	var tab = _hit(tabs, _press)
	if not tab.is_empty(): state.set_sheet(tab.id); return
	var tempo = _hit(tempos, _press)
	if not tempo.is_empty():
		_tempo = tempo.tempo
		_tempo_position = _tempo.position
		_mode = "tempo"
		return
	if _press.x >= header_width() and _press.y < marker_top():
		_mode = "scrub"
		scrub_to(_press.x)
		return
	var marker = _hit(markers, _press) if _press.x >= header_width() else {}
	if not marker.is_empty():
		_pressed = marker.marker.id
		if _pressed not in state.selected_markers or input.shift_pressed: state.select_marker(_pressed, input.shift_pressed)
		_mode = "marker_resize" if Edits.is_region(marker.marker) and _press.x >= marker.rect.end.x - 6 * ui_scale else "marker_move"
		_original.clear()
		for id in state.selected_markers:
			var item = state.find_marker(id)
			_original.append({"id": id, "position": item.position, "length": item.get("length", 0.0)})
		_preview = _original.duplicate(true)
		return
	if _press.x < header_width():
		if not last_pointer_track.is_empty(): state.select_track(last_pointer_track.id)
		return
	var block = _hit(blocks, _press) if _press.y >= tracks_top() else {}
	if not block.is_empty():
		_pressed = block.instrument.id
		if _pressed not in state.selected_instruments or input.shift_pressed: state.select_instrument(_pressed, input.shift_pressed)
		if _pressed not in state.selected_instruments: return
		_mode = "pending"
		if block.rect.size.x > 18 * ui_scale:
			if _press.x < block.rect.position.x + 6 * ui_scale: _mode = "left"
			elif _press.x > block.rect.end.x - 6 * ui_scale: _mode = "right"
		_original.clear()
		for id in state.selected_instruments:
			var item = state.find_instrument(id)
			_original.append({"id": id, "start": item.placement.start, "length": item.placement.length})
		_preview = _original.duplicate(true)
		return
	if not input.shift_pressed: state.clear_selection()
	state.cursor = last_pointer_unit
	_mode = "band"
	_band = Rect2(_press, Vector2.ZERO)
	_band_additive = input.shift_pressed
	queue_redraw()

func _motion(position: Vector2) -> void:
	if _mode == "pending" and position.distance_to(_press) >= 4 * ui_scale: _mode = "move"
	if _mode == "band": _band = Rect2(_press, position - _press)
	elif _mode == "scrub": scrub_to(position.x)
	elif _mode == "tempo": _tempo_position = maxf(0.0, snap(unit_of(position.x)))
	elif _mode in ["move", "left", "right", "marker_move", "marker_resize"]:
		_preview = _original.duplicate(true)
		var primary: Dictionary = {}
		for item in _original:
			if item.id == _pressed: primary = item
		if primary.is_empty(): return
		var unit = clamp_unit(snap(unit_of(position.x)))
		var key = "position" if _mode.begins_with("marker") else "start"
		if _mode in ["move", "marker_move"]:
			var delta = clamp_unit(snap(float(primary[key]) + (position.x - _press.x) / zoom)) - float(primary[key])
			var minimum = 0.0 if state.sheet == NONE else float(state.find_parameter(state.sheet).get("minimum", 0.0))
			for item in _original: delta = maxf(delta, minimum - float(item[key]))
			for item in _preview: item[key] += delta
		else:
			for item in _preview:
				if item.id != _pressed: continue
				if _mode == "left":
					var duration = float(item.length)
					if duration <= 0: duration = lengths.natural_length(state.find_instrument(item.id)) if state.sheet == NONE else 0.1
					var end = float(item.start) + duration
					item.start = minf(unit, end - 0.01)
					item.length = end - item.start
				else: item.length = maxf(0.01, unit - float(item[key]))
	queue_redraw()

func _left_release(position: Vector2) -> void:
	if _mode in ["move", "left", "right"] and _preview != _original:
		var track = _row(position)
		edits.set_placements(_preview, track.get("id", NONE) if _mode == "move" else NONE)
	elif _mode == "move" and _preview.size() == 1:
		var track = _row(position)
		if not track.is_empty(): edits.move_instrument(_pressed, track)
	elif _mode in ["marker_move", "marker_resize"] and _preview != _original: edits.set_marker_positions(_preview)
	elif _mode == "tempo": edits.set_tempo_position(_tempo, _tempo_position)
	elif _mode == "band" and _band.size.length() > 2:
		var ids: Array = []
		for block in blocks:
			if _band.abs().intersects(block.rect): ids.append(block.instrument.id)
		state.select_instruments(ids, _band_additive)
		for item in markers:
			if _band.abs().intersects(item.rect) and item.marker.id not in state.selected_markers: state.select_marker(item.marker.id, true)
	_mode = ""
	_preview.clear()
	_original.clear()
	_tempo = {}
	invalidate()

func zoom_at(x: float, factor: float) -> void:
	var anchor = unit_of(x)
	zoom = clampf(zoom * factor, 2.0, 20000.0)
	scroll_x = anchor - (x - header_width()) / zoom
	clamp_scroll()
	invalidate()

func clamp_scroll() -> void:
	if state.sheet == NONE: scroll_x = maxf(0.0, scroll_x)
	else:
		var parameter = state.find_parameter(state.sheet)
		var minimum = float(parameter.get("minimum", 0.0))
		scroll_x = clampf(scroll_x, minimum, maxf(minimum, float(parameter.get("maximum", 1.0)) - visible_units()))

func zoom_to_fit() -> void:
	if state.sheet == NONE:
		zoom = maxf(2.0, (size.x - header_width()) / maxf(1.0, lengths.event_length(state.event) * 1.05))
		scroll_x = 0.0
	else:
		var parameter = state.find_parameter(state.sheet)
		zoom = maxf(2.0, (size.x - header_width() - 8) / maxf(0.001, float(parameter.get("maximum", 1.0)) - float(parameter.get("minimum", 0.0))))
		scroll_x = parameter.get("minimum", 0.0)
	invalidate()

func _payload(data) -> Dictionary:
	if not data is Dictionary: return {}
	if data.get("type", "") == "files":
		var files = data.get("files", [])
		if not files.is_empty(): return {"type": "asset", "path": files[0]}
	return data

func _can_drop_data(_at_position: Vector2, data) -> bool:
	if state == null or state.event.is_empty(): return false
	var payload = _payload(data)
	return payload.get("type", "") == "asset" or (payload.get("type", "") == "event" and payload.get("id", NONE) != state.event_id)

func _drop_data(at_position: Vector2, data) -> void:
	if not _can_drop_data(at_position, data): return
	ensure_layout()
	var payload = _payload(data)
	var block = _hit(blocks, at_position)
	if payload.type == "asset" and not block.is_empty() and block.instrument.get("$type", "") in ["Multi", "Scatterer"]:
		edits.list_add("Add playlist entry", block.instrument.entries, {"asset": payload.path, "weight": 1.0})
		return
	var track = _row(at_position)
	if track.is_empty(): track = state.event.tracks.back() if not state.event.tracks.is_empty() else edits.add_track()
	var instrument = Edits.create_instrument("Clip" if payload.type == "asset" else "Event", state.sheet, clamp_unit(snap(unit_of(maxf(header_width(), at_position.x)))), 0.0 if state.sheet == NONE else default_sheet_length())
	if payload.type == "asset":
		instrument.asset = payload.path
		instrument.name = str(payload.path).get_file().get_basename()
	else:
		instrument.event = payload.id
		instrument.name = str(state.find_event(payload.id).get("path", "Event")).get_file()
	edits.add_instrument(track, instrument)
	state.select_instrument(instrument.id)

func open_menu(position: Vector2) -> void:
	last_pointer_unit = clamp_unit(snap(unit_of(maxf(header_width(), position.x))))
	last_pointer_track = _row(position)
	state.cursor = last_pointer_unit
	var block = _hit(blocks, position)
	var marker = _hit(markers, position)
	if not block.is_empty() and block.instrument.id not in state.selected_instruments: state.select_instrument(block.instrument.id)
	if not marker.is_empty() and marker.marker.id not in state.selected_markers: state.select_marker(marker.marker.id)
	_menu.clear()
	for child in _menu.get_children():
		_menu.remove_child(child)
		child.queue_free()
	var instruments = PopupMenu.new()
	instruments.name = "Instruments"
	_menu.add_child(instruments)
	for index in Edits.INSTRUMENT_KINDS.size(): instruments.add_item(Edits.INSTRUMENT_KINDS[index], 100 + index)
	instruments.id_pressed.connect(_on_menu)
	_menu.add_submenu_item("Add instrument", "Instruments")
	if state.sheet == NONE:
		var logic = PopupMenu.new()
		logic.name = "Markers"
		_menu.add_child(logic)
		for index in Edits.MARKER_KINDS.size(): logic.add_item(Edits.MARKER_KINDS[index], 200 + index)
		logic.id_pressed.connect(_on_menu)
		_menu.add_submenu_item("Add marker", "Markers")
		_menu.add_item("Add tempo marker here", 7)
	_menu.add_item("Add track", 1)
	if not last_pointer_track.is_empty(): _menu.add_item("Remove track", 2)
	_menu.add_separator()
	_menu.add_item("Copy", 4)
	_menu.add_item("Paste", 5)
	_menu.add_item("Duplicate", 6)
	_menu.add_item("Delete", 3)
	_menu.add_check_item("Snap to grid", 9)
	_menu.set_item_checked(_menu.get_item_index(9), state.snap_to_grid)
	_menu.add_item("Zoom to fit", 8)
	_menu.position = Vector2i(get_screen_position() + position)
	_menu.popup()

func _on_menu(id: int) -> void:
	if id >= 200:
		var length = maxf(0.25, 240.0 / maxf(1, state.event.tempo[0].bpm)) if has_tempo() else 2.0
		var marker = Edits.create_marker(Edits.MARKER_KINDS[id - 200], last_pointer_unit, length)
		edits.add_marker(marker)
		state.select_marker(marker.id)
	elif id >= 100:
		var track = last_pointer_track
		if track.is_empty(): track = state.event.tracks.back() if not state.event.tracks.is_empty() else edits.add_track()
		var kind: String = Edits.INSTRUMENT_KINDS[id - 100]
		var instrument = Edits.create_instrument(kind, state.sheet, last_pointer_unit, default_sheet_length() if state.sheet != NONE else 1.0 if kind == "Silence" else 0.0)
		edits.add_instrument(track, instrument)
		state.select_instrument(instrument.id)
	else:
		match id:
			1: state.select_track(edits.add_track().id)
			2: edits.remove_track(last_pointer_track)
			3: delete_requested.emit()
			4: copy_requested.emit()
			5: paste_requested.emit()
			6: duplicate_requested.emit()
			7: edits.add_tempo(last_pointer_unit)
			8: zoom_to_fit()
			9: state.snap_to_grid = not state.snap_to_grid; state.bump()
	invalidate()

func running_instance():
	if audition == null: return null
	var system = audition.get("System")
	if system == null: return null
	for instance in system.instances:
		if instance.description.id == state.event_id and instance.state != "Stopped": return instance
	return null

func scrub_to(x: float) -> void:
	var unit = clamp_unit(unit_of(x))
	state.cursor = snap(unit) if state.sheet == NONE else unit
	if audition != null:
		var system = audition.get("System")
		if system != null:
			var parameter = state.find_parameter(state.sheet)
			for instance in system.instances:
				if instance.description.id != state.event_id or instance.state == "Stopped": continue
				if state.sheet == NONE: instance.timeline_position = unit
				else: instance.set_parameter(parameter.get("name", ""), unit, true)
			if state.sheet != NONE and state.is_global_parameter(state.sheet): system.set_global_parameter(parameter.get("name", ""), unit, true)
	queue_redraw()

func _process(delta: float) -> void:
	if state == null: return
	_clock += delta
	if _clock < 1.0 / 30.0: return
	_clock = 0.0
	var instance = running_instance()
	var value: float = NAN
	if instance != null:
		value = instance.timeline_position if state.sheet == NONE else instance.get_parameter(state.find_parameter(state.sheet).get("name", ""))
	elif audition != null and state.sheet != NONE and state.is_global_parameter(state.sheet):
		var system = audition.get("System")
		if system != null: value = system.get_global_parameter(state.find_parameter(state.sheet).get("name", ""))
	if (is_nan(value) and is_nan(_live_value)) or value == _live_value: return
	_live_value = value
	queue_redraw()
