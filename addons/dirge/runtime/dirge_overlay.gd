@tool
extends CanvasLayer
## Debug-only F9 overlay sharing the editor's HTML/CSS panes, never EditorInterface.
const Document = preload("res://addons/dirge/tool/Core/project_document.gd")
const MixerPane = preload("res://addons/dirge/tool/Mixer/mixer_pane.gd")
const ProfilerPane = preload("res://addons/dirge/tool/Profiler/profiler_pane.gd")
const ToggleAction = "dirge_overlay"
@export var server_path: NodePath = NodePath("/root/DirgeServer")
var server
var document
var mixer
var profiler
var tabs: TabContainer
var _status: Label
var _enabled: bool = false
var _reload_pending: bool = false
var _own_reload: bool = false
var _status_clock: float = 0.0
var IsShown: bool:
	get: return visible and tabs != null

func _ready() -> void:
	layer = 120
	visible = false
	process_mode = Node.PROCESS_MODE_ALWAYS
	_enabled = OS.is_debug_build() and not Engine.is_editor_hint() and DisplayServer.get_name() != "headless"
	set_process(_enabled)
	set_process_unhandled_input(_enabled)
	if _enabled and not InputMap.has_action(ToggleAction):
		InputMap.add_action(ToggleAction)
		var key = InputEventKey.new()
		key.physical_keycode = KEY_F9
		InputMap.action_add_event(ToggleAction, key)

func _unhandled_input(event: InputEvent) -> void:
	if _enabled and event.is_action_pressed(ToggleAction, false):
		Toggle()
		get_viewport().set_input_as_handled()

func Toggle() -> void:
	if IsShown: HideOverlay()
	else: ShowOverlay()
func ShowOverlay() -> void:
	if not _enabled: return
	if tabs == null and not _build(): return
	visible = true
func HideOverlay() -> void:
	visible = false

func find_server():
	var preferred = get_node_or_null(server_path)
	var best = preferred if _loaded(preferred) else null
	var candidates: Array = [get_tree().root]
	while not candidates.is_empty():
		var node = candidates.pop_back()
		if _loaded(node) and (best == null or int(node.GetStats().get("instances", 0)) > int(best.GetStats().get("instances", 0))): best = node
		var children = node.get_children()
		for index in range(children.size() - 1, -1, -1): candidates.append(children[index])
	return best
func _loaded(node) -> bool:
	return is_instance_valid(node) and node.has_method("GetStats") and node.has_method("HotReload") and node.get("IsLoaded") == true

func _build() -> bool:
	server = find_server()
	if server == null:
		push_warning("Dirge overlay: no loaded DirgeServer in the scene tree.")
		return false
	document = Document.new()
	document.replace(server.System.project, server.ProjectPath)
	document.changed.connect(func():
		if not _own_reload: _reload_pending = true)
	server.Loaded.connect(_on_server_loaded)
	var root = Control.new()
	root.name = "Root"
	root.mouse_filter = Control.MOUSE_FILTER_IGNORE
	root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(root)
	var panel = PanelContainer.new()
	panel.name = "Panel"
	root.add_child(panel)
	panel.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_WIDE)
	panel.anchor_top = 0.5
	panel.offset_top = 0
	var column = VBoxContainer.new()
	panel.add_child(column)
	var header = HBoxContainer.new()
	column.add_child(header)
	var title = Label.new()
	title.text = "Dirge"
	header.add_child(title)
	_status = Label.new()
	_status.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_status.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	header.add_child(_status)
	var close = Button.new()
	close.text = "Close (F9)"
	close.pressed.connect(HideOverlay)
	header.add_child(close)
	tabs = TabContainer.new()
	tabs.name = "Tabs"
	tabs.size_flags_vertical = Control.SIZE_EXPAND_FILL
	column.add_child(tabs)
	mixer = MixerPane.new()
	mixer.name = "Mixer"
	mixer.setup(document, server)
	tabs.add_child(mixer)
	profiler = ProfilerPane.new()
	profiler.name = "Profiler"
	profiler.setup(server, document)
	tabs.add_child(profiler)
	return true

func _on_server_loaded(system) -> void:
	if _own_reload or document == null: return
	_own_reload = true
	document.replace(system.project, server.ProjectPath)
	_own_reload = false
	_reload_pending = false

func _process(delta: float) -> void:
	if _reload_pending and _loaded(server):
		_reload_pending = false
		_own_reload = true
		server.HotReload(document.project)
		_own_reload = false
	if not IsShown: return
	_status_clock += delta
	if _status_clock < 0.1: return
	_status_clock = 0
	if not _loaded(server):
		_status.text = "no project"
		return
	var stats = server.GetStats()
	_status.text = "%d inst · %d voices · %.2f ms · %d buses" % [stats.get("instances", 0), stats.get("real_voices", 0), stats.get("update_ms", 0), AudioServer.bus_count]

func _exit_tree() -> void:
	if is_instance_valid(server) and server.Loaded.is_connected(_on_server_loaded): server.Loaded.disconnect(_on_server_loaded)
