@tool
extends Control
const Math = preload("res://addons/dirge/tool/Transport/preview_math.gd")
const Scale = preload("res://addons/dirge/tool/Core/tool_scale.gd")
const ThemeColors = preload("res://addons/dirge/tool/Core/tool_theme.gd")
signal changed
var emitter: Dictionary = {"position": Vector3(3, 0, -4), "velocity": Vector3.ZERO, "forward": Vector3.FORWARD, "up": Vector3.UP}
var listener: Dictionary = {"position": Vector3.ZERO, "velocity": Vector3.ZERO, "forward": Vector3.FORWARD, "up": Vector3.UP}
var min_distance: float = 1.0
var max_distance: float = 20.0
var _dragging: bool = false
var _pixels: float = 1.0
var readout: Dictionary:
	get: return Math.compute(emitter, listener, min_distance, max_distance)

func _init() -> void:
	name = "PreviewPanner"
	set_meta("dirge_authored_minimum", Vector2(220, 150))
	custom_minimum_size = Vector2(220, 150) * Scale.compute()
	mouse_filter = Control.MOUSE_FILTER_STOP
	focus_mode = Control.FOCUS_CLICK
	clip_contents = true

func set_distances(minimum: float, maximum: float) -> void:
	min_distance = minimum
	max_distance = maximum
	queue_redraw()

func _notification(what: int) -> void:
	if what == NOTIFICATION_RESIZED or what == NOTIFICATION_THEME_CHANGED:
		queue_redraw()

func _gui_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_LEFT:
			_dragging = event.pressed
			if _dragging:
				grab_focus()
				move_emitter(event.position)
			accept_event()
		elif event.pressed and event.button_index in [MOUSE_BUTTON_WHEEL_UP, MOUSE_BUTTON_WHEEL_DOWN]:
			rotate_listener(7.5 if event.button_index == MOUSE_BUTTON_WHEEL_UP else -7.5)
			accept_event()
	elif event is InputEventMouseMotion and _dragging and event.button_mask & MOUSE_BUTTON_MASK_LEFT:
		move_emitter(event.position)
		accept_event()

func move_emitter(screen: Vector2) -> void:
	var offset = (screen - size * 0.5) / maxf(_pixels, 0.001)
	emitter.position = Vector3(offset.x, 0, offset.y)
	queue_redraw()
	changed.emit()

func rotate_listener(degrees: float) -> void:
	listener.forward = Math.rotate_yaw(listener.forward, degrees)
	queue_redraw()
	changed.emit()

func _draw() -> void:
	var ui = Scale.compute()
	var colors = ThemeColors.palette()
	var center = size * 0.5
	var position: Vector3 = emitter.position
	_pixels = maxf(0.001, minf(size.x, size.y) * 0.44 / maxf(maxf(max_distance, Vector2(position.x, position.z).length()), 1.0))
	draw_rect(Rect2(Vector2.ZERO, size), colors.bg)
	var step = _pixels
	while step < 14.0 * ui: step *= 2.0
	var x = fmod(center.x, step)
	while x < size.x:
		draw_line(Vector2(x, 0), Vector2(x, size.y), colors.grid)
		x += step
	var y = fmod(center.y, step)
	while y < size.y:
		draw_line(Vector2(0, y), Vector2(size.x, y), colors.grid)
		y += step
	draw_arc(center, maxf(0, min_distance * _pixels), 0, TAU, 64, Color(colors.accent, 0.6), 1, true)
	draw_arc(center, maxf(0, max_distance * _pixels), 0, TAU, 96, colors.muted, 1, true)
	var forward = Vector2(listener.forward.x, listener.forward.z)
	if forward.is_zero_approx(): forward = Vector2.UP
	forward = forward.normalized()
	var right = Vector2(-forward.y, forward.x)
	var tip = center + forward * 26 * ui
	draw_line(center, tip, colors.fg, 2 * ui, true)
	draw_colored_polygon(PackedVector2Array([tip + forward * 6 * ui, tip - right * 5 * ui, tip + right * 5 * ui]), colors.fg)
	draw_circle(center, 5 * ui, colors.fg)
	var dot = center + Vector2(position.x, position.z) * _pixels
	var facing = Vector2(emitter.forward.x, emitter.forward.z)
	if not facing.is_zero_approx(): draw_line(dot, dot + facing.normalized() * 12 * ui, colors.accent, 1, true)
	draw_line(center, dot, Color(colors.accent, 0.35), 1, true)
	draw_circle(dot, 7 * ui, colors.accent)
	draw_circle(dot, 7 * ui, colors.bg, false, 1.5, true)
	var values = readout
	var lines = ["dist %.2f m   norm %.2f" % [values.Distance, values.DistanceNormalized], "dir %.1f°   elev %.1f°" % [values.Direction, values.Elevation], "cone %.1f°   orient %.1f°" % [values.EventConeAngle, values.EventOrientation], "speed %.2f   |v| %.2f" % [values.Speed, values.SpeedAbsolute]]
	var font = get_theme_default_font()
	var font_size = Scale.px(11)
	var line = font_size + 4 * ui
	for index in lines.size():
		draw_string(font, Vector2(6 * ui, line * (index + 1)), lines[index], HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, colors.muted if index == 3 else colors.fg)
	draw_string(font, Vector2(6 * ui, size.y - 6 * ui), "x %.1f  z %.1f   min/max %.1f/%.1f" % [position.x, position.z, min_distance, max_distance], HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, colors.muted)
