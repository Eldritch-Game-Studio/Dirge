@tool
extends Control
const Document = preload("res://addons/dirge/tool/Core/project_document.gd")
const SelectionState = preload("res://addons/dirge/tool/Core/selection.gd")
const Docs = preload("res://addons/dirge/tool/Core/tool_documents.gd")
const Settings = preload("res://addons/dirge/tool/Core/tool_settings.gd")
const Scale = preload("res://addons/dirge/tool/Core/tool_scale.gd")
const Palette = preload("res://addons/dirge/tool/Core/tool_theme.gd")
const Commands = preload("res://addons/dirge/tool/Core/tool_commands.gd")
const Server = preload("res://addons/dirge/runtime/dirge_server.gd")
const Browser = preload("res://addons/dirge/tool/Browser/browser_pane.gd")
const Transport = preload("res://addons/dirge/tool/Transport/transport_pane.gd")
const Events = preload("res://addons/dirge/tool/Events/event_editor_pane.gd")
const Mixer = preload("res://addons/dirge/tool/Mixer/mixer_pane.gd")
const Profiler = preload("res://addons/dirge/tool/Profiler/profiler_pane.gd")
const Validation = preload("res://addons/dirge/tool/Shell/validation_panel.gd")
const LiveBridge = preload("res://addons/dirge/tool/Shell/live_update_bridge.gd")
const PreviewMath = preload("res://addons/dirge/tool/Transport/preview_math.gd")

var document = Document.new()
var selection: String = ""
var selection_state = SelectionState.new()
var audition
var browser
var transport
var events
var mixer
var profiler
var validation
var live
var menu: Control
var status: Control
var bottom_tabs: TabContainer
var _main_split: HSplitContainer
var _center_split: VSplitContainer
var _world: SubViewport
var _listener: AudioListener3D
var _editor_layout: AudioBusLayout
var _buses_dock: Node
var _dock_processing: bool = true
var _scale_watcher: Node
var _theme_watcher: Node
var _open_dialog: FileDialog
var _save_dialog: FileDialog
var _discard_dialog: ConfirmationDialog
var _error_dialog: AcceptDialog
var _connect_dialog: ConfirmationDialog
var _connect_host: LineEdit
var _connect_port: SpinBox
var _after_discard: Callable
var _save_then_build: bool = false
var _configured: bool = false
var _refresh_in: float = 0.0
var _message_ttl: float = 0.0
var _reload_in: float = -1.0
var _restoring_splits: bool = false
var _reload_when_idle: bool = false

func _ready() -> void:
	setup()

func setup(_editor_interface: Object = null) -> void:
	if _configured: return
	_configured = true
	name = "Dirge"
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	focus_mode = Control.FOCUS_ALL
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_scale_watcher = Scale.new()
	_theme_watcher = Palette.new()
	add_child(_scale_watcher)
	add_child(_theme_watcher)
	_create_audition()
	live = LiveBridge.new()
	add_child(live)
	live.setup(document)
	_build_layout()
	_create_dialogs()
	_scale_watcher.changed.connect(_appearance_changed)
	_theme_watcher.changed.connect(_appearance_changed)
	document.changed.connect(_document_changed)
	document.saved.connect(_refresh_document_ui)
	selection_state.changed.connect(_selection_changed)
	live.state_changed.connect(_live_changed)
	_refresh_document_ui()
	_restore_last_project()

func _create_audition() -> void:
	_editor_layout = AudioServer.generate_bus_layout()
	if Engine.is_editor_hint():
		_buses_dock = _find_audio_dock(EditorInterface.get_base_control())
		if _buses_dock != null:
			_dock_processing = _buses_dock.is_processing()
			_buses_dock.set_process(false)
	_world = SubViewport.new()
	_world.name = "DirgeAuditionWorld"
	_world.size = Vector2i(2, 2)
	_world.own_world_3d = true
	_world.audio_listener_enable_3d = true
	_world.audio_listener_enable_2d = false
	_world.render_target_update_mode = SubViewport.UPDATE_DISABLED
	_world.disable_3d = false
	add_child(_world)
	var camera = Camera3D.new()
	camera.current = true
	_world.add_child(camera)
	_listener = AudioListener3D.new()
	_world.add_child(_listener)
	_listener.make_current()
	audition = Server.new()
	audition.editor_audition = true
	audition.name = "Audition"
	_world.add_child(audition)
	audition.DisableLiveUpdate()
	audition.Load(document.project.duplicate(true))

static func _find_audio_dock(root: Node) -> Node:
	if root.get_class() == "EditorAudioBuses": return root
	for child in root.get_children():
		var found = _find_audio_dock(child)
		if found != null: return found
	return null

func _build_layout() -> void:
	var layout = VBoxContainer.new()
	layout.name = "Layout"
	layout.add_theme_constant_override("separation", 0)
	add_child(layout)
	layout.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	menu = Docs.create("shell.html")
	menu.connect("clicked", _menu_clicked)
	var menu_slot = Docs.slot(menu, "MenuBar", Vector2(0, 28))
	menu_slot.size_flags_vertical = Control.SIZE_FILL
	layout.add_child(menu_slot)
	_main_split = HSplitContainer.new()
	_main_split.size_flags_vertical = Control.SIZE_EXPAND_FILL
	layout.add_child(_main_split)
	browser = Browser.new()
	browser.name = "Browser"
	var browser_frame = _frame(browser, Vector2(260, 0))
	browser_frame.size_flags_horizontal = Control.SIZE_FILL
	_main_split.add_child(browser_frame)
	browser.setup(document, audition, selection_state)
	browser.event_selected.connect(select_event)
	browser.subject_selected.connect(_browser_subject)
	_center_split = VSplitContainer.new()
	_center_split.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_main_split.add_child(_center_split)
	var top = VSplitContainer.new()
	top.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_center_split.add_child(top)
	transport = Transport.new()
	transport.name = "Transport"
	var transport_frame = _frame(transport, Vector2(0, 150))
	transport_frame.size_flags_vertical = Control.SIZE_FILL
	top.add_child(transport_frame)
	transport.setup(document, audition)
	transport.preview_changed.connect(func(_emitter: Dictionary, listener: Dictionary): _listener.transform = PreviewMath.to_transform(listener))
	events = Events.new()
	events.name = "Events"
	top.add_child(_frame(events, Vector2(0, 120)))
	events.setup(document, audition)
	events.selection_changed.connect(_event_selection_changed)
	bottom_tabs = TabContainer.new()
	bottom_tabs.name = "Bottom"
	bottom_tabs.size_flags_vertical = Control.SIZE_FILL
	bottom_tabs.set_meta("dirge_authored_minimum", Vector2(0, 180))
	bottom_tabs.custom_minimum_size = Vector2(0, 180) * Scale.compute()
	_center_split.add_child(bottom_tabs)
	mixer = Mixer.new()
	mixer.name = "Mixer"
	bottom_tabs.add_child(_frame(mixer, Vector2.ZERO))
	bottom_tabs.set_tab_title(0, "Mixer")
	mixer.setup(document, audition)
	mixer.selection_changed.connect(_mixer_selection_changed)
	profiler = Profiler.new()
	profiler.name = "Profiler"
	bottom_tabs.add_child(_frame(profiler, Vector2.ZERO))
	bottom_tabs.set_tab_title(1, "Profiler")
	profiler.setup(audition, document)
	if profiler.has_method("set_live_client"): profiler.set_live_client(live.client)
	if profiler.has_signal("event_selected"): profiler.connect("event_selected", select_event)
	validation = Validation.new()
	validation.name = "Validation"
	bottom_tabs.add_child(_frame(validation, Vector2.ZERO))
	bottom_tabs.set_tab_title(2, "Validation")
	status = Docs.create("shell-status.html")
	var status_slot = Docs.slot(status, "StatusBar", Vector2(0, 22))
	status_slot.size_flags_vertical = Control.SIZE_FILL
	layout.add_child(status_slot)
	validation.counts_changed.connect(func(errors: int, warnings: int):
		status.call("set_text", "#status-errors", "%d errors" % errors)
		status.call("set_text", "#status-warnings", "%d warnings" % warnings))
	validation.subject_selected.connect(_navigate_subject)
	validation.setup(document)
	_main_split.dragged.connect(func(offset: int):
		if not _restoring_splits: Settings.set_value(Settings.BROWSER_SPLIT, offset / Scale.compute()))
	_center_split.dragged.connect(func(offset: int):
		if not _restoring_splits: Settings.set_value(Settings.BOTTOM_SPLIT, offset / Scale.compute()))
	_center_split.resized.connect(_default_bottom_split)
	_restore_splits.call_deferred()

func _frame(pane: Control, minimum: Vector2) -> Control:
	var frame = Control.new()
	frame.name = pane.name + "Frame"
	frame.set_meta("dirge_authored_minimum", minimum)
	frame.custom_minimum_size = minimum * Scale.compute()
	frame.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	frame.size_flags_vertical = Control.SIZE_EXPAND_FILL
	frame.clip_contents = true
	frame.add_child(pane)
	pane.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	return frame

func _restore_splits() -> void:
	_restoring_splits = true
	_main_split.split_offset = roundi(float(Settings.get_value(Settings.BROWSER_SPLIT, 0.0)) * Scale.compute())
	if Settings.has_setting(Settings.BOTTOM_SPLIT):
		_center_split.split_offset = roundi(float(Settings.get_value(Settings.BOTTOM_SPLIT)) * Scale.compute())
	else: _default_bottom_split()
	_restoring_splits = false

func _default_bottom_split() -> void:
	if _center_split == null or bottom_tabs == null or Settings.has_setting(Settings.BOTTOM_SPLIT): return
	var height = _center_split.size.y
	if height <= 0.0: return
	var separation = _center_split.get_theme_constant("separation")
	var fixed_top = 150.0 * Scale.compute() + separation + events.get_chrome_height()
	var max_top = height - separation - bottom_tabs.custom_minimum_size.y
	var wished_top = minf(fixed_top + 0.4 * height, max_top)
	_center_split.split_offset = roundi(wished_top + separation - height)

func _appearance_changed() -> void:
	Docs.refresh(self)
	_restore_splits()
	menu.call("set_text", "#view-theme", "Theme: " + Palette.PRESETS[Palette.preset()])

func _create_dialogs() -> void:
	_open_dialog = FileDialog.new()
	_open_dialog.title = "Open Dirge Project"
	_open_dialog.file_mode = FileDialog.FILE_MODE_OPEN_FILE
	_open_dialog.access = FileDialog.ACCESS_FILESYSTEM
	_open_dialog.filters = PackedStringArray(["*.dirge.json ; Dirge Project", "*.json ; JSON"])
	_open_dialog.file_selected.connect(load_project_file)
	add_child(_open_dialog)
	_save_dialog = FileDialog.new()
	_save_dialog.title = "Save Dirge Project"
	_save_dialog.file_mode = FileDialog.FILE_MODE_SAVE_FILE
	_save_dialog.access = FileDialog.ACCESS_FILESYSTEM
	_save_dialog.filters = PackedStringArray(["*.dirge.json ; Dirge Project"])
	_save_dialog.file_selected.connect(write_project_file)
	_save_dialog.canceled.connect(func(): _save_then_build = false)
	add_child(_save_dialog)
	_discard_dialog = ConfirmationDialog.new()
	_discard_dialog.title = "Discard unsaved changes?"
	_discard_dialog.dialog_text = "The current project has unsaved changes. Discard them?"
	_discard_dialog.ok_button_text = "Discard"
	_discard_dialog.confirmed.connect(func():
		var action = _after_discard
		_after_discard = Callable()
		if action.is_valid(): action.call())
	_discard_dialog.canceled.connect(func(): _after_discard = Callable())
	add_child(_discard_dialog)
	_error_dialog = AcceptDialog.new()
	_error_dialog.title = "Dirge"
	add_child(_error_dialog)
	_connect_dialog = ConfirmationDialog.new()
	_connect_dialog.title = "Connect live update"
	_connect_dialog.ok_button_text = "Connect"
	var fields = VBoxContainer.new()
	_connect_host = LineEdit.new()
	_connect_host.text = "127.0.0.1"
	_connect_host.placeholder_text = "Host"
	fields.add_child(_connect_host)
	_connect_port = SpinBox.new()
	_connect_port.min_value = 1
	_connect_port.max_value = 65535
	_connect_port.value = 7447
	fields.add_child(_connect_port)
	_connect_dialog.add_child(fields)
	_connect_dialog.confirmed.connect(func(): live.connect_to_game(_connect_host.text, int(_connect_port.value)))
	add_child(_connect_dialog)

func _confirm_discard(action: Callable) -> void:
	if not document.dirty:
		action.call()
		return
	_after_discard = action
	_discard_dialog.popup_centered()

func new_project() -> void:
	_confirm_discard(func():
		audition.StopAllEvents(true)
		selection_state.clear()
		select_event("")
		document.reset()
		set_message("New project"))

func open_project() -> void:
	_confirm_discard(func():
		if not document.path.is_empty(): _open_dialog.current_dir = ProjectSettings.globalize_path(document.path.get_base_dir())
		_open_dialog.popup_centered_ratio(0.6))

func load_project_file(file_path: String) -> bool:
	var error = document.load_file(file_path)
	if error != OK:
		_show_error("Cannot load '%s': %s" % [file_path, error_string(error)])
		return false
	audition.StopAllEvents(true)
	selection_state.clear()
	select_event("")
	Settings.set_value("dirge/last_project", file_path)
	_reload_audition()
	set_message("Opened " + file_path)
	return true

func save_project(save_as: bool = false) -> void:
	if not save_as and not document.path.is_empty():
		write_project_file(document.path)
		return
	if not document.path.is_empty(): _save_dialog.current_path = ProjectSettings.globalize_path(document.path)
	else:
		var regex = RegEx.new()
		regex.compile("[^\\p{L}\\p{N}_-]")
		_save_dialog.current_file = regex.sub(str(document.project.get("name", "untitled")), "_", true).to_lower() + ".dirge.json"
	_save_dialog.popup_centered_ratio(0.6)

func write_project_file(file_path: String) -> bool:
	if not file_path.to_lower().ends_with(".dirge.json"):
		file_path = file_path.substr(0, file_path.length() - 5) + ".dirge.json" if file_path.to_lower().ends_with(".json") else file_path + ".dirge.json"
	var error = document.save(file_path)
	if error != OK:
		_show_error("Cannot write '%s': %s" % [file_path, error_string(error)])
		return false
	Settings.set_value("dirge/last_project", file_path)
	var resource_path = ProjectSettings.localize_path(file_path)
	if Engine.is_editor_hint() and resource_path.begins_with("res://"):
		EditorInterface.get_resource_filesystem().update_file(resource_path)
	if _save_then_build:
		_save_then_build = false
		_report_banks(file_path)
	else: set_message("Saved " + file_path)
	return true

func build_banks() -> void:
	if document.path.is_empty():
		_save_then_build = true
		save_project(true)
	elif write_project_file(document.path): _report_banks(document.path)

func _report_banks(file_path: String) -> void:
	var count = 0
	for bank in document.project.banks: count += bank.events.size()
	set_message("Built %d bank(s), %d event reference(s) -> %s" % [document.project.banks.size(), count, file_path])

func validate_project() -> void:
	validation.refresh(true)
	bottom_tabs.current_tab = 2
	set_message("Validation: no issues" if validation.diagnostics.is_empty() else "Validation: %d error(s), %d warning(s)" % [validation.error_count, validation.warning_count], validation.error_count > 0)

func toggle_live_connection() -> void:
	if live.connected:
		live.disconnect_from_game()
	else:
		_connect_host.text = live.host
		_connect_port.value = live.port
		_connect_dialog.popup_centered()

func undo() -> void:
	document.undo()

func redo() -> void:
	document.redo()

func select_event(id: String) -> void:
	selection = id
	if id.is_empty():
		if selection_state.kind == SelectionState.Kind.EVENT or not selection_state.owner.is_empty():
			selection_state.clear()
	elif selection_state.kind != SelectionState.Kind.EVENT or selection_state.id != id:
		selection_state.select(SelectionState.Kind.EVENT, id)
	if events.state.event_id != id and not (id.is_empty() and events.state.event.is_empty()):
		events.set_event(id)
	transport.set_event(id)

func _selection_changed() -> void:
	validation.select_subject(selection_state.id)
	if selection_state.kind == SelectionState.Kind.EVENT:
		selection = selection_state.id
		if events.state.event_id != selection:
			events.set_event(selection)
		transport.set_event(selection)

func _event_selection_changed() -> void:
	if events.state == null or events.state.event.is_empty(): return
	var state = events.state
	var kind = SelectionState.Kind.EVENT
	var id: String = state.event_id
	var owner_id = ""
	if not state.selected_instruments.is_empty():
		kind = SelectionState.Kind.INSTRUMENT
		id = state.selected_instruments[0]
		owner_id = state.event_id
	elif not state.selected_markers.is_empty():
		kind = SelectionState.Kind.MARKER
		id = state.selected_markers[0]
		owner_id = state.event_id
	elif state.selected_track != state.NONE:
		kind = SelectionState.Kind.TRACK
		id = state.selected_track
		owner_id = state.event_id
	if selection_state.kind != kind or selection_state.id != id or selection_state.owner != owner_id:
		selection_state.select(kind, id, owner_id)

func _mixer_selection_changed(kind: String, id: String) -> void:
	var kinds = {"Bus":SelectionState.Kind.BUS, "Effect":SelectionState.Kind.EFFECT, "Vca":SelectionState.Kind.VCA, "Snapshot":SelectionState.Kind.SNAPSHOT}
	if kinds.has(kind):
		selection_state.select(kinds[kind], id, mixer.selected_bus if kind == "Effect" else "")

func _browser_subject(kind: int, id: String) -> void:
	_navigate_subject({"kind":kind, "id":id, "owner":"", "index":-1})

func _navigate_subject(subject: Dictionary) -> void:
	selection_state.select(subject.kind, subject.id, subject.owner, subject.index)
	if subject.kind == SelectionState.Kind.EVENT:
		select_event(subject.id)
	elif subject.kind in [SelectionState.Kind.INSTRUMENT, SelectionState.Kind.TRACK, SelectionState.Kind.MARKER] or subject.kind == SelectionState.Kind.PARAMETER:
		if not str(subject.owner).is_empty():
			selection = subject.owner
			events.set_event(selection)
			transport.set_event(selection)
		if events.has_method("select_subject"): events.select_subject(subject.kind, subject.id, subject.owner, subject.index)
	elif subject.kind in [SelectionState.Kind.BUS, SelectionState.Kind.EFFECT, SelectionState.Kind.VCA, SelectionState.Kind.SNAPSHOT]:
		bottom_tabs.current_tab = 0
		if mixer.has_method("select_subject"): mixer.select_subject(subject.kind, subject.id, subject.owner, subject.index)
		elif subject.kind == SelectionState.Kind.SNAPSHOT: mixer.select_snapshot(subject.id)

func _document_changed() -> void:
	_refresh_document_ui()
	if document.changed_scope not in [Document.ChangeScope.NONE, Document.ChangeScope.ASSETS]:
		if _try_live_mixer():
			_reload_when_idle = true
		else:
			_reload_in = 0.15
	if not selection.is_empty() and not document.project.events.any(func(event): return event.id == selection): select_event("")

func _reload_audition() -> void:
	_reload_in = -1.0
	_reload_when_idle = false
	if audition != null:
		audition.HotReload(document.project.duplicate(true))
		transport._panner_changed()

func _try_live_mixer() -> bool:
	if audition == null or not audition.IsLoaded or audition.System.instances.is_empty() or document.changed_scope & Document.ChangeScope.MIXER == 0:
		return false
	var before: Dictionary = audition.System.project.duplicate(true)
	var after: Dictionary = document.project.duplicate(true)
	var changes: Array = []
	var built_buses: Array = before.get("mixer", {}).get("buses", [])
	var buses: Array = after.get("mixer", {}).get("buses", [])
	if built_buses.size() != buses.size(): return false
	for index in buses.size():
		var bus: Dictionary = buses[index]
		var built: Dictionary = built_buses[index]
		if bus.id != built.id or bus.effects.size() != built.effects.size(): return false
		changes.append(bus.duplicate(true))
		for key in ["volumeDb", "mute", "solo", "bypassEffects"]:
			bus[key] = built[key]
		for effect_index in bus.effects.size():
			var parameters: Dictionary = bus.effects[effect_index].parameters
			var original: Dictionary = built.effects[effect_index].parameters
			if parameters.size() != original.size() or not parameters.has_all(original.keys()):
				return false
			for key in ["enabled", "parameters"]:
				bus.effects[effect_index][key] = built.effects[effect_index][key]
	if before != after: return false
	var commands: Array = []
	for bus in changes:
		audition.LiveSetBusVolume(bus.name, bus.volumeDb)
		audition.System.mixer.set_bus_mute(bus.name, bus.mute)
		audition.System.mixer.set_bus_solo(bus.name, bus.solo)
		commands.append({"type":"BusSetBypass", "bus":bus.name, "bypass":bus.bypassEffects})
		for index in bus.effects.size():
			var effect: Dictionary = bus.effects[index]
			commands.append({"type":"EffectSetEnabled", "bus":bus.name, "effectIndex":index, "enabled":effect.enabled})
			for key in effect.parameters:
				commands.append({"type":"EffectSetParameter", "bus":bus.name, "effectIndex":index, "parameter":key, "value":effect.parameters[key]})
	audition.Driver.apply(commands)
	return true

func _refresh_document_ui() -> void:
	if menu == null: return
	Docs.disabled(menu, "#edit-undo", not document.can_undo)
	Docs.disabled(menu, "#edit-redo", not document.can_redo)
	menu.call("set_text", "#edit-undo", "Undo " + document.undo_label if document.can_undo else "Undo")
	menu.call("set_text", "#edit-redo", "Redo " + document.redo_label if document.can_redo else "Redo")
	menu.call("set_text", "#project-name", str(document.project.get("name", "Untitled")) + (" *" if document.dirty else ""))
	menu.call("set_text", "#view-theme", "Theme: " + Palette.PRESETS[Palette.preset()])
	Docs.class_state(menu, "#project-name", "dirty", document.dirty)
	Docs.hidden(status, "#status-dirty", not document.dirty)
	status.call("set_text", "#status-path", document.path if not document.path.is_empty() else "Untitled")

func _menu_clicked(id: String) -> void:
	match id:
		"file-new": new_project()
		"file-open": open_project()
		"file-save": save_project()
		"file-save-as": save_project(true)
		"edit-undo": undo()
		"edit-redo": redo()
		"project-validate": validate_project()
		"project-build-banks": build_banks()
		"live-connect": toggle_live_connection()
		"view-theme":
			Palette.set_preset((Palette.preset() + 1) % Palette.PRESETS.size())
			_theme_watcher.refresh()

func _live_changed() -> void:
	menu.call("set_text", "#live-connect", "Disconnect" if live.connected else "Connect…")
	Docs.hidden(menu, "#live-state", not live.connected)
	menu.call("set_attribute", "#live-state", "title", "%s:%s" % [live.host, live.port])
	if not live.last_error.is_empty(): set_message(live.last_error, true)

func set_message(text: String, error: bool = false) -> void:
	_message_ttl = 0.0 if text.is_empty() else 10.0
	status.call("set_text", "#status-message", text)
	Docs.class_state(status, "#status-message", "error", error)

func _show_error(message: String) -> void:
	set_message(message, true)
	_error_dialog.dialog_text = message
	_error_dialog.popup_centered()

func _process(delta: float) -> void:
	if not _configured or status == null: return
	if _reload_in >= 0.0:
		_reload_in -= delta
		if _reload_in < 0.0: _reload_audition()
	elif _reload_when_idle and audition.IsLoaded and audition.System.instances.is_empty():
		_reload_audition()
	if _message_ttl > 0.0:
		_message_ttl -= delta
		if _message_ttl <= 0.0: set_message("")
	_refresh_in -= delta
	if _refresh_in > 0.0: return
	_refresh_in = 0.1
	var stats = audition.GetStats()
	status.call("set_text", "#status-instances", str(stats.get("instances", 0)))
	status.call("set_text", "#status-voices", str(stats.get("real_voices", 0)))
	status.call("set_text", "#status-ms", "%.1f ms" % stats.get("update_ms", 0.0))
	status.call("set_text", "#status-buses", str(AudioServer.bus_count))
	status.call("set_text", "#status-live", live.status_text)
	Docs.hidden(status, "#status-live", live.status_text.is_empty())

func _input(event: InputEvent) -> void:
	if not is_visible_in_tree() or not event is InputEventKey or not event.pressed or event.echo: return
	# Dialogs own their shortcuts and text navigation.
	for dialog in [_open_dialog, _save_dialog, _discard_dialog, _error_dialog, _connect_dialog]:
		if dialog != null and dialog.visible: return
	var command = Commands.from_key(event)
	if command != Commands.Command.SAVE and _text_field_has_focus(): return
	if (event.ctrl_pressed or event.meta_pressed) and event.keycode == KEY_N:
		new_project()
		get_viewport().set_input_as_handled()
	elif (event.ctrl_pressed or event.meta_pressed) and event.keycode == KEY_O:
		open_project()
		get_viewport().set_input_as_handled()
	elif command >= 0 and dispatch(command): get_viewport().set_input_as_handled()

func _text_field_has_focus() -> bool:
	var focused = get_viewport().gui_get_focus_owner()
	if focused is LineEdit or focused is TextEdit: return true
	if focused != null and focused.has_method("get_active_element_id"):
		var id = str(focused.call("get_active_element_id"))
		if not id.is_empty():
			return focused.call("has_element", "input#" + id + ", textarea#" + id + ", select#" + id)
	return false

func focused_pane() -> Control:
	var focused = get_viewport().gui_get_focus_owner()
	if focused == null: return null
	for pane in [browser, transport, events, mixer, profiler, validation]:
		if focused == pane or pane.is_ancestor_of(focused): return pane
	return null

func dispatch(command: int) -> bool:
	var pane = focused_pane()
	if pane != null and pane.has_method("handle") and pane.handle(command): return true
	match command:
		Commands.Command.UNDO: undo()
		Commands.Command.REDO: redo()
		Commands.Command.SAVE: save_project()
		Commands.Command.PLAY_STOP: return transport.handle(command)
		_: return false
	return true

func _restore_last_project() -> void:
	for file_path in [str(Settings.get_value("dirge/last_project", "")), str(ProjectSettings.get_setting("dirge/project_path", ""))]:
		if not file_path.is_empty() and FileAccess.file_exists(file_path) and load_project_file(file_path): return
	var found: Array[String] = []
	_find_projects("res://", found)
	if found.size() == 1: load_project_file(found[0])
	elif found.size() > 1: set_message("%d Dirge projects — File › Open… to choose one." % found.size())
	else: set_message("No Dirge project yet — File › New, then Save As….")

static func _find_projects(directory: String, results: Array[String]) -> void:
	var dir = DirAccess.open(directory)
	if dir == null or dir.file_exists(".gdignore"): return
	dir.list_dir_begin()
	var entry = dir.get_next()
	while not entry.is_empty():
		if not entry.begins_with(".") and not dir.is_link(entry):
			var path = directory.path_join(entry)
			if dir.current_is_dir():
				if entry not in ["addons", "bin", "obj"]: _find_projects(path, results)
			elif entry.ends_with(".dirge.json"): results.append(path)
		entry = dir.get_next()
	dir.list_dir_end()

func _exit_tree() -> void:
	if not _configured: return
	if document.changed.is_connected(_document_changed): document.changed.disconnect(_document_changed)
	if document.saved.is_connected(_refresh_document_ui): document.saved.disconnect(_refresh_document_ui)
	if selection_state.changed.is_connected(_selection_changed): selection_state.changed.disconnect(_selection_changed)
	if is_instance_valid(audition):
		audition.Unload()
		if audition.Driver != null: audition.Driver.shutdown()
	if _editor_layout != null:
		AudioServer.set_bus_layout(_editor_layout)
		AudioServer.bus_layout_changed.emit()
		_editor_layout = null
	if is_instance_valid(_buses_dock): _buses_dock.set_process(_dock_processing)
