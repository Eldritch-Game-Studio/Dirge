@tool
extends Control

const NONE = "00000000000000000000000000000000"
const Palette = preload("res://addons/dirge/tool/Core/tool_theme.gd")
var state
var edits
var audition
var ui_scale: float = 1.0
var selected: int = -1
var _selected_lane: String = NONE
var _dragging: int = -1
var _work: Array = []
var _menu: PopupMenu
var _segment: int = -1
var _cursor: float = NAN
var _clock: float = 0.0

func setup(editor_state, event_edits, server = null) -> void:
	state = editor_state
	edits = event_edits
	audition = server
	focus_mode = Control.FOCUS_ALL
	clip_contents = true
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	custom_minimum_size = Vector2(240, 120)
	state.selection_changed.connect(invalidate)
	resized.connect(invalidate)

func _ready() -> void:
	_menu = PopupMenu.new()
	add_child(_menu)
	_menu.id_pressed.connect(_on_menu)

func invalidate() -> void:
	if state != null and _selected_lane != state.selected_lane:
		_selected_lane = state.selected_lane
		selected = -1
		_dragging = -1
		_work.clear()
	queue_redraw()

func lane() -> Dictionary:
	return state.find_lane(state.selected_lane) if state != null else {}

func points() -> Array:
	return _work if _dragging >= 0 else lane().get("curve", {}).get("points", [])

func has_selected_point() -> bool:
	return selected >= 0 and selected < points().size()

func property_range() -> Vector2:
	var current = lane()
	var kind: String = current.get("target", {}).get("kind", "")
	if kind.ends_with("VolumeDb"): return Vector2(-60, 12)
	if kind.ends_with("Pitch"): return Vector2(0, 4)
	var minimum = 0.0
	var maximum = 1.0
	for point in current.get("curve", {}).get("points", []):
		minimum = minf(minimum, point[1])
		maximum = maxf(maximum, point[1])
	var padding = (maximum - minimum) * 0.1
	return Vector2(minimum - padding, maximum + padding)

func parameter_range() -> Vector2:
	var parameter = state.find_parameter(lane().get("parameter", NONE))
	var minimum = float(parameter.get("minimum", 0.0))
	return Vector2(minimum, maxf(minimum + 0.001, parameter.get("maximum", 1.0)))

func plot() -> Rect2:
	return Rect2(Vector2(44, 8) * ui_scale, Vector2(maxf(1, size.x - 52 * ui_scale), maxf(1, size.y - 26 * ui_scale)))

func to_screen(point: Array) -> Vector2:
	var x = parameter_range()
	var y = property_range()
	var rect = plot()
	return rect.position + Vector2((float(point[0]) - x.x) / (x.y - x.x) * rect.size.x, (1.0 - (float(point[1]) - y.x) / (y.y - y.x)) * rect.size.y)

func from_screen(position: Vector2) -> Vector2:
	var x = parameter_range()
	var y = property_range()
	var rect = plot()
	var value = Vector2(lerpf(x.x, x.y, (position.x - rect.position.x) / rect.size.x), lerpf(y.y, y.x, (position.y - rect.position.y) / rect.size.y))
	if state.snap_to_grid:
		var parameter = state.find_parameter(lane().get("parameter", NONE))
		var step = 0.1 * pow(10.0, floorf(log(maxf(0.001, x.y - x.x)) / log(10.0))) if parameter.get("kind", "") == "Continuous" else 1.0
		value.x = snappedf(value.x, step)
		value.y = snappedf(value.y, 1.0 if y.y - y.x > 20 else 0.05)
	return Vector2(clampf(value.x, x.x, x.y), clampf(value.y, y.x, y.y))

func target_label(target: Dictionary) -> String:
	var kind: String = target.get("kind", "")
	var owner: String = target.get("owner", NONE)
	match kind:
		"EventVolumeDb": return "Event volume"
		"EventPitch": return "Event pitch"
		"InstrumentVolumeDb", "InstrumentPitch": return state.find_instrument(owner).get("name", "Instrument") + (" pitch" if kind.ends_with("Pitch") else " volume")
		"BusVolumeDb", "BusPitch": return state.find_bus(owner).get("name", "Bus") + (" pitch" if kind.ends_with("Pitch") else " volume")
		"EffectParameter": return "%s fx%d.%s" % [state.find_bus(owner).get("name", "Bus"), target.get("effectIndex", -1), target.get("name", "")]
		"VcaVolumeDb": return "VCA volume"
	return kind

func _draw() -> void:
	var palette = Palette.palette()
	var bg: Color = palette.bg
	var muted: Color = palette.muted
	var accent: Color = palette.accent
	draw_rect(Rect2(Vector2.ZERO, size), bg)
	if lane().is_empty():
		_text(Vector2(12, size.y / 2), "Select an automation lane to edit its curve.", muted)
		return
	var rect = plot()
	for index in 11:
		var x = rect.position.x + rect.size.x * index / 10.0
		draw_line(Vector2(x, rect.position.y), Vector2(x, rect.end.y), palette.grid)
	for index in 5:
		var y = rect.position.y + rect.size.y * index / 4.0
		draw_line(Vector2(rect.position.x, y), Vector2(rect.end.x, y), palette.grid)
	draw_rect(rect, muted, false)
	var xr = parameter_range()
	var yr = property_range()
	_text(Vector2(2, rect.position.y + 11 * ui_scale), String.num(yr.y), muted)
	_text(Vector2(2, rect.end.y), String.num(yr.x), muted)
	_text(Vector2(rect.position.x, size.y - 4), String.num(xr.x), muted)
	_text(Vector2(rect.end.x - 40, size.y - 4), String.num(xr.y), muted)
	var parameter = state.find_parameter(lane().get("parameter", NONE))
	_text(rect.position + Vector2(6, 13) * ui_scale, target_label(lane().target) + " by " + parameter.get("name", "(no parameter)"), accent)
	var values = points()
	if values.is_empty():
		_text(rect.position + Vector2(6, rect.size.y * 0.5), "Double-click to add points.", muted)
	else:
		var first = to_screen(values.front())
		var last = to_screen(values.back())
		draw_line(Vector2(rect.position.x, first.y), first, accent, 1.5)
		draw_line(last, Vector2(rect.end.x, last.y), accent, 1.5)
		for index in values.size() - 1:
			var a = to_screen(values[index])
			var b = to_screen(values[index + 1])
			var shape: String = values[index][2] if values[index].size() > 2 else "Linear"
			if shape == "Stepped":
				draw_line(a, Vector2(b.x, a.y), accent, 1.5)
				draw_line(Vector2(b.x, a.y), b, accent, 1.5)
			elif shape == "Smooth":
				var previous = a
				for sample in range(1, 17):
					var t = sample / 16.0
					var current = Vector2(lerpf(a.x, b.x, t), lerpf(a.y, b.y, t * t * (3 - 2 * t)))
					draw_line(previous, current, accent, 1.5)
					previous = current
			else: draw_line(a, b, accent, 1.5)
		for index in values.size(): draw_circle(to_screen(values[index]), 5 * ui_scale, palette.cursor if index == selected else accent)
	if not is_nan(_cursor):
		var point = to_screen([_cursor, evaluate(values, _cursor)])
		if point.x >= rect.position.x and point.x <= rect.end.x:
			draw_line(Vector2(point.x, rect.position.y), Vector2(point.x, rect.end.y), palette.playhead, 1.5)
			draw_circle(point, 4 * ui_scale, palette.playhead)

func _text(position: Vector2, value: String, color: Color) -> void:
	draw_string(get_theme_default_font(), position, value, HORIZONTAL_ALIGNMENT_LEFT, -1, int(11 * ui_scale), color)

static func evaluate(values: Array, x: float) -> float:
	if values.is_empty(): return 0.0
	if x <= float(values[0][0]): return values[0][1]
	for index in range(1, values.size()):
		var a: Array = values[index - 1]
		var b: Array = values[index]
		if x > float(b[0]): continue
		var shape = a[2] if a.size() > 2 else "Linear"
		if shape == "Stepped": return a[1]
		var t = clampf((x - float(a[0])) / maxf(0.000001, float(b[0]) - float(a[0])), 0, 1)
		if shape == "Smooth": t = t * t * (3 - 2 * t)
		return lerpf(a[1], b[1], t)
	return values.back()[1]

func point_at(position: Vector2) -> int:
	var values = points()
	for index in range(values.size() - 1, -1, -1):
		if to_screen(values[index]).distance_to(position) <= 8 * ui_scale: return index
	return -1

func add_point(value: Vector2) -> void:
	var values = points().duplicate(true)
	var shape = "Linear"
	for point in values:
		if float(point[0]) <= value.x and point.size() > 2: shape = point[2]
	values.append([value.x, value.y, shape])
	edits.set_curve_points(lane(), values)
	selected = lane().curve.points.find([value.x, value.y, shape])
	queue_redraw()

func delete_selected_point() -> void:
	if not has_selected_point(): return
	var values = points().duplicate(true)
	values.remove_at(selected)
	edits.set_curve_points(lane(), values)
	selected = -1
	queue_redraw()

func set_shape(index: int, shape: String) -> void:
	var values = points().duplicate(true)
	if index < 0 or index >= values.size(): return
	values[index] = [values[index][0], values[index][1], shape]
	edits.set_curve_points(lane(), values)
	queue_redraw()

func _gui_input(input: InputEvent) -> void:
	if lane().is_empty(): return
	if input is InputEventKey and input.pressed and input.keycode == KEY_DELETE:
		delete_selected_point()
		accept_event()
	elif input is InputEventMouseButton:
		if input.button_index == MOUSE_BUTTON_LEFT:
			if input.pressed:
				grab_focus()
				selected = point_at(input.position)
				if input.double_click and selected < 0: add_point(from_screen(input.position))
				elif selected >= 0:
					_work = points().duplicate(true)
					_dragging = selected
			elif _dragging >= 0:
				var values = _work
				_dragging = -1
				edits.set_curve_points(lane(), values)
				_work = []
			queue_redraw()
			accept_event()
		elif input.button_index == MOUSE_BUTTON_RIGHT and input.pressed:
			open_menu(input.position)
			accept_event()
	elif input is InputEventMouseMotion and _dragging >= 0:
		var value = from_screen(input.position)
		if _dragging > 0: value.x = maxf(value.x, _work[_dragging - 1][0])
		if _dragging + 1 < _work.size(): value.x = minf(value.x, _work[_dragging + 1][0])
		_work[_dragging][0] = value.x
		_work[_dragging][1] = value.y
		queue_redraw()
		accept_event()

func open_menu(position: Vector2) -> void:
	var values = points()
	selected = point_at(position)
	_segment = -1
	var x = from_screen(position).x
	for index in values.size() - 1:
		if values[index][0] <= x and x <= values[index + 1][0]: _segment = index
	if selected >= 0 and selected + 1 < values.size(): _segment = selected
	_menu.clear()
	if _segment >= 0:
		for index in 3:
			var shape = ["Linear", "Smooth", "Stepped"][index]
			_menu.add_radio_check_item(shape, index)
			_menu.set_item_checked(index, values[_segment].size() > 2 and values[_segment][2] == shape)
	if selected >= 0: _menu.add_item("Delete point", 3)
	if _menu.item_count == 0: return
	_menu.position = Vector2i(get_screen_position() + position)
	_menu.popup()
	queue_redraw()

func _on_menu(id: int) -> void:
	if id == 3: delete_selected_point()
	else: set_shape(_segment, ["Linear", "Smooth", "Stepped"][id])

func _process(delta: float) -> void:
	_clock += delta
	if _clock < 1.0 / 30.0 or state == null: return
	_clock = 0.0
	var current = lane()
	var value: float = NAN
	if not current.is_empty():
		var parameter = state.find_parameter(current.get("parameter", NONE))
		if not parameter.is_empty():
			if state.sheet == parameter.id: value = state.cursor
			var system = audition.get("System") if audition != null else null
			if system != null:
				if state.is_global_parameter(parameter.id): value = system.get_global_parameter(parameter.name)
				for instance in system.instances:
					if instance.description.id == state.event_id and instance.state != "Stopped":
						value = instance.get_parameter(parameter.name)
						break
	if (is_nan(value) and is_nan(_cursor)) or value == _cursor: return
	_cursor = value
	queue_redraw()
