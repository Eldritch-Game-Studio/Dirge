@tool
extends Control

const State = preload("res://addons/dirge/tool/Events/event_editor_state.gd")
const Edits = preload("res://addons/dirge/tool/Events/event_edits.gd")
const Lengths = preload("res://addons/dirge/tool/Events/asset_length_cache.gd")
const Assets = preload("res://addons/dirge/tool/Events/asset_picker.gd")
const Timeline = preload("res://addons/dirge/tool/Events/timeline_canvas.gd")
const CurveEditor = preload("res://addons/dirge/tool/Events/curve_canvas.gd")
const Header = preload("res://addons/dirge/tool/Events/event_header_view.gd")
const Inspector = preload("res://addons/dirge/tool/Events/event_inspector_view.gd")
const Automation = preload("res://addons/dirge/tool/Events/automation_view.gd")
const Documents = preload("res://addons/dirge/tool/Core/tool_documents.gd")
const Scale = preload("res://addons/dirge/tool/Core/tool_scale.gd")
const Palette = preload("res://addons/dirge/tool/Core/tool_theme.gd")
const Model = preload("res://addons/dirge/model/Project/project_model.gd")
signal selection_changed
var document
var audition
var state
var edits
var lengths
var assets
var timeline
var curve
var header
var inspector
var automation
var _header_slot: Control
var _clipboard: Array = []
var _clipboard_markers: Array = []
var _clipboard_origin: float = 0.0
var _render_queued: bool = false
var _playing: bool = false
var _scale: float = 1.0
var _palette: Dictionary = {}
var _view_signature: int = 0

func setup(project_document, server = null) -> void:
	_detach()
	document = project_document
	audition = server
	state = State.new(document)
	edits = Edits.new(state)
	lengths = Lengths.new(state)
	assets = Assets.new(self)
	timeline = Timeline.new()
	timeline.name = "Timeline"
	timeline.setup(state, edits, lengths, audition)
	curve = CurveEditor.new()
	curve.name = "Curve"
	curve.setup(state, edits, audition)
	header = Header.new(state, edits, Documents.create("event-header.html"), toggle_play, timeline.zoom_to_fit)
	inspector = Inspector.new(state, edits, assets, Documents.create("event-inspector.html"))
	automation = Automation.new(state, edits, curve, Documents.create("event-automation.html"))
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	var root = VBoxContainer.new()
	root.name = "EventEditorLayout"
	add_child(root)
	root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_header_slot = Documents.slot(header.host, "Header", Vector2(0, header.authored_height()))
	_header_slot.size_flags_vertical = Control.SIZE_FILL
	root.add_child(_header_slot)
	var lower = VSplitContainer.new()
	lower.size_flags_vertical = Control.SIZE_EXPAND_FILL
	root.add_child(lower)
	var middle = HSplitContainer.new()
	middle.size_flags_vertical = Control.SIZE_EXPAND_FILL
	lower.add_child(middle)
	middle.add_child(timeline)
	middle.add_child(Documents.slot(inspector.host, "Inspector", Vector2(320, 0)))
	var bottom = HSplitContainer.new()
	bottom.custom_minimum_size = Vector2(0, 170) * Scale.compute()
	bottom.set_meta("dirge_authored_minimum", Vector2(0, 170))
	bottom.size_flags_vertical = Control.SIZE_FILL
	lower.add_child(bottom)
	bottom.add_child(Documents.slot(automation.host, "Automation", Vector2(380, 0)))
	bottom.add_child(curve)
	header.sections_changed.connect(_header_sections_changed)
	document.changed.connect(_document_changed)
	state.selection_changed.connect(_selection_changed)
	timeline.copy_requested.connect(copy_selection)
	timeline.paste_requested.connect(paste)
	timeline.duplicate_requested.connect(duplicate_selection)
	timeline.delete_requested.connect(delete_selection)
	_scale = Scale.compute()
	_palette = Palette.palette()
	timeline.ui_scale = _scale
	curve.ui_scale = _scale
	_view_signature = 0
	_refresh_all()

func _detach() -> void:
	if document != null and document.changed.is_connected(_document_changed): document.changed.disconnect(_document_changed)
	if assets != null: assets.dispose()
	_dispose_forms()
	for child in get_children():
		remove_child(child)
		child.queue_free()

func _exit_tree() -> void:
	if document != null and document.changed.is_connected(_document_changed): document.changed.disconnect(_document_changed)
	_dispose_forms()

func _dispose_forms() -> void:
	for view in [header, inspector, automation]:
		if view != null: view.form.dispose()

func set_event(id: String) -> void:
	if state == null: return
	state.set_event(id)
	_refresh_all()

func _header_sections_changed() -> void:
	_header_slot.custom_minimum_size = Vector2(0, header.authored_height() * _scale)
	_header_slot.set_meta("dirge_authored_minimum", Vector2(0, header.authored_height()))

func _document_changed() -> void:
	state.refresh()
	lengths.clear()
	queue_render()

func _selection_changed() -> void:
	selection_changed.emit()
	queue_render()

func queue_render() -> void:
	if _render_queued: return
	_render_queued = true
	_refresh_all.call_deferred()
	if timeline != null: timeline.invalidate()
	if curve != null: curve.invalidate()

func _signature() -> int:
	var names: Array = []
	for bus in state.project.get("mixer", {}).get("buses", []):
		var effects: Array = []
		for effect in bus.get("effects", []): effects.append([effect.get("typeName", ""), effect.get("parameters", {})])
		names.append([bus.id, bus.get("name", ""), effects])
	for snapshot in state.project.get("snapshots", []): names.append([snapshot.id, snapshot.get("name", "")])
	for event in state.project.get("events", []): names.append([event.id, event.get("path", "")])
	return hash([state.event, state.project.get("globalParameters", []), names, state.selected_instruments, state.selected_markers, state.selected_track, state.selected_lane, state.sheet, state.snap_to_grid, _playing])

func _refresh_all() -> void:
	_render_queued = false
	if state == null or not is_instance_valid(header.host): return
	var signature = _signature()
	if signature != _view_signature:
		_view_signature = signature
		header.playing = _playing
		header.render()
		inspector.render()
		automation.render()
	timeline.invalidate()
	curve.invalidate()

func _process(_delta: float) -> void:
	if state == null: return
	var playing = timeline.running_instance() != null
	if playing != _playing:
		_playing = playing
		queue_render()
	var scale_value = Scale.compute()
	var palette = Palette.palette()
	if scale_value != _scale or palette != _palette:
		_scale = scale_value
		_palette = palette
		timeline.ui_scale = _scale
		curve.ui_scale = _scale
		timeline.custom_minimum_size = Vector2(320, 160) * _scale
		curve.custom_minimum_size = Vector2(240, 120) * _scale
		Documents.refresh(self)
		_header_sections_changed()
		timeline.invalidate()
		curve.invalidate()

func toggle_play() -> void:
	if audition == null or state.event.is_empty(): return
	var system = audition.get("System")
	var stopped = false
	if system != null:
		for instance in system.instances:
			if instance.description.id == state.event_id and instance.state != "Stopped":
				instance.stop("AllowFadeout")
				stopped = true
	if not stopped: audition.StartEvent(state.event.path)

func select_subject(kind: int, id: String, owner_id: String = "", _index: int = -1) -> void:
	if state == null: return
	if kind == 1:
		set_event(id)
		return
	if not owner_id.is_empty() and owner_id != Model.NONE and owner_id != state.event_id:
		state.set_event(owner_id)
	match kind:
		2:
			var instrument = state.find_instrument(id)
			if not instrument.is_empty():
				state.set_sheet(instrument.placement.parameter)
				state.select_instrument(id)
		3: state.select_track(id)
		4:
			state.set_sheet(Model.NONE)
			state.select_marker(id)
		9:
			state.set_sheet(id)
			header.show_parameters = true
			_header_sections_changed()
			_view_signature = 0
	_refresh_all()

func handle(command) -> bool:
	if command is int:
		var commands = ["undo", "redo", "save", "playstop", "delete", "copy", "paste", "duplicate", "selectall", "escape"]
		if command < 0 or command >= commands.size(): return false
		command = commands[command]
	match str(command).to_lower().replace("_", ""):
		"undo": return document.undo()
		"redo": return document.redo()
		"playstop": toggle_play(); return true
		"delete": return delete_selection()
		"copy": return copy_selection()
		"cut":
			if copy_selection(): return delete_selection()
			return false
		"paste": return paste()
		"duplicate": return duplicate_selection()
		"selectall": return select_all()
		"escape": state.clear_selection(); return true
	return false

func _unhandled_key_input(input: InputEvent) -> void:
	if not is_visible_in_tree() or not input is InputEventKey or not input.pressed or input.echo: return
	var focused = get_viewport().gui_get_focus_owner()
	if focused != timeline and focused != curve: return
	var command = ""
	if input.ctrl_pressed or input.meta_pressed:
		match input.keycode:
			KEY_C: command = "copy"
			KEY_X: command = "cut"
			KEY_V: command = "paste"
			KEY_D: command = "duplicate"
			KEY_A: command = "selectall"
			KEY_Z: command = "redo" if input.shift_pressed else "undo"
			KEY_Y: command = "redo"
	else:
		match input.keycode:
			KEY_DELETE: command = "delete"
			KEY_ESCAPE: command = "escape"
			KEY_SPACE: command = "playstop"
	if not command.is_empty() and handle(command): get_viewport().set_input_as_handled()

func delete_selection() -> bool:
	if curve.has_focus() and curve.has_selected_point():
		curve.delete_selected_point()
		return true
	return edits.delete_selection()

func select_all() -> bool:
	if state.event.is_empty(): return false
	var ids: Array = []
	for track in state.event.tracks:
		for instrument in track.instruments:
			if instrument.placement.parameter == state.sheet: ids.append(instrument.id)
	state.select_instruments(ids)
	return true

func copy_selection() -> bool:
	if state.event.is_empty() or (state.selected_instruments.is_empty() and state.selected_markers.is_empty()): return false
	_clipboard.clear()
	_clipboard_markers.clear()
	_clipboard_origin = INF
	for id in state.selected_instruments:
		var instrument = state.find_instrument(id)
		var track = state.instrument_track(id)
		if instrument.is_empty() or track.is_empty(): continue
		_clipboard.append({"instrument": Edits.clone_item(instrument), "track": state.event.tracks.find(track)})
		_clipboard_origin = minf(_clipboard_origin, instrument.placement.start)
	for id in state.selected_markers:
		var marker = state.find_marker(id)
		if marker.is_empty(): continue
		_clipboard_markers.append(Edits.clone_item(marker))
		_clipboard_origin = minf(_clipboard_origin, marker.position)
	if is_inf(_clipboard_origin): _clipboard_origin = 0.0
	return true

func paste() -> bool:
	if state.event.is_empty() or (_clipboard.is_empty() and _clipboard_markers.is_empty()): return false
	var event: Dictionary = state.event
	var created: Array = []
	var created_markers: Array = []
	var minimum_track = 2147483647
	for item in _clipboard: minimum_track = mini(minimum_track, item.track)
	var anchor_track = event.tracks.find(timeline.last_pointer_track)
	if anchor_track < 0: anchor_track = event.tracks.find(state.find_track(state.selected_track))
	if anchor_track < 0: anchor_track = minimum_track if minimum_track != 2147483647 else 0
	document.transact("Paste selection", func():
		if not _clipboard.is_empty() and event.tracks.is_empty(): event.tracks.append(Model.create("Track", {"name": "Track 1"}))
		for item in _clipboard:
			var instrument = Edits.clone_item(item.instrument)
			instrument.placement.parameter = state.sheet
			instrument.placement.start = maxf(0, state.cursor + float(instrument.placement.start) - _clipboard_origin)
			var index = clampi(anchor_track + int(item.track) - minimum_track, 0, event.tracks.size() - 1)
			event.tracks[index].instruments.append(instrument)
			created.append(instrument.id)
		if state.sheet == Model.NONE:
			for item in _clipboard_markers:
				var marker = Edits.clone_item(item)
				marker.position = maxf(0, state.cursor + float(marker.position) - _clipboard_origin)
				event.markers.append(marker)
				created_markers.append(marker.id))
	state.select_instruments(created)
	for id in created_markers: state.select_marker(id, true)
	return true

func duplicate_selection() -> bool:
	if state.event.is_empty() or (state.selected_instruments.is_empty() and state.selected_markers.is_empty()): return false
	var start = INF
	var end = -INF
	var items: Array = []
	var logic: Array = []
	for id in state.selected_instruments:
		var instrument = state.find_instrument(id)
		if instrument.is_empty(): continue
		var duration = float(instrument.placement.length)
		if duration <= 0: duration = lengths.natural_length(instrument) if state.sheet == Model.NONE else 0.1
		start = minf(start, instrument.placement.start)
		end = maxf(end, float(instrument.placement.start) + duration)
		items.append({"track": state.instrument_track(id), "instrument": instrument})
	for id in state.selected_markers:
		var marker = state.find_marker(id)
		if marker.is_empty(): continue
		start = minf(start, marker.position)
		end = maxf(end, float(marker.position) + maxf(0.5, marker.get("length", 0.0)))
		logic.append(marker)
	var offset = end - start if end > start else 1.0
	var created: Array = []
	var created_markers: Array = []
	var event: Dictionary = state.event
	document.transact("Duplicate selection", func():
		for item in items:
			var instrument = Edits.clone_item(item.instrument)
			instrument.placement.start += offset
			item.track.instruments.append(instrument)
			created.append(instrument.id)
		for source in logic:
			var marker = Edits.clone_item(source)
			marker.position += offset
			event.markers.append(marker)
			created_markers.append(marker.id))
	state.select_instruments(created)
	for id in created_markers: state.select_marker(id, true)
	return true

func get_chrome_height() -> float:
	var root = get_node_or_null("EventEditorLayout")
	if root == null or _header_slot == null: return 0.0
	var lower = root.get_child(1)
	return _header_slot.custom_minimum_size.y + 170.0 * _scale + root.get_theme_constant("separation") + lower.get_theme_constant("separation")
