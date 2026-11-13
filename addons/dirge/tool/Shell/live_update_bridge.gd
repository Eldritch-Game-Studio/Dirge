@tool
extends Node
const Client = preload("res://addons/dirge/runtime/live_update_client.gd")
signal state_changed
var client: Node
var document: RefCounted
var project_name: String = ""
var last_error: String = ""
var host: String = "127.0.0.1"
var port: int = 7447
var stats: Dictionary = {}
var _push_in: float = -1.0
var _last_project: Dictionary = {}
var connected: bool:
	get: return client != null and client.is_connected_to_game
var status_text: String:
	get:
		if not connected:
			return "live: connecting to %s:%s" % [host, port] if client != null and client.state == "Connecting" else ""
		if stats.is_empty():
			return "live: " + project_name
		return "live: %s · %d inst · %d voices · %.2f ms" % [project_name, stats.get("instances", 0), stats.get("realVoices", 0), stats.get("updateMs", 0.0)]

func setup(value: RefCounted) -> void:
	document = value
	_last_project = document.project.duplicate(true)
	client = Client.new()
	client.name = "LiveUpdateClient"
	add_child(client)
	client.hello.connect(func(project: String):
		project_name = project
		last_error = ""
		state_changed.emit())
	client.stats.connect(func(value_stats: Dictionary): stats = value_stats)
	client.disconnected.connect(func(reason: String):
		stats.clear()
		last_error = "Live update: " + reason if not reason.is_empty() else ""
		state_changed.emit())
	client.error_received.connect(func(message: String):
		last_error = "Live update: " + message
		state_changed.emit())
	document.changed.connect(_changed)

func connect_to_game(address: String = "127.0.0.1", remote_port: int = 7447) -> Error:
	host = address if not address.is_empty() else "127.0.0.1"
	port = remote_port
	last_error = ""
	stats.clear()
	var error = client.connect_to_game(host, port)
	if error != OK:
		last_error = "Live update: cannot connect to %s:%d (%s)." % [host, port, error_string(error)]
	state_changed.emit()
	return error

func disconnect_from_game() -> void:
	client.disconnect_from_game()
	stats.clear()
	_push_in = -1.0
	state_changed.emit()

func _process(delta: float) -> void:
	if _push_in < 0.0:
		return
	_push_in -= delta
	if _push_in < 0.0 and connected:
		client.send_project(document.project)

func _changed() -> void:
	if connected and document.changed_scope != document.ChangeScope.ASSETS:
		if not _push_values_only():
			_push_in = 0.25
	_last_project = document.project.duplicate(true)

# Use narrow commands only when the whole edit really is numeric bus/effect values.
# Structural mixer edits must not be silently lost by a subject-only shortcut.
func _push_values_only() -> bool:
	if document.changed_scope != document.ChangeScope.MIXER or document.changed_subject.is_empty():
		return false
	var current = document.project.duplicate(true)
	var previous = _last_project.duplicate(true)
	var target: Dictionary = {}
	var old_target: Dictionary = {}
	for bus in current.get("mixer", {}).get("buses", []):
		if bus.id == document.changed_subject:
			target = bus
	for bus in previous.get("mixer", {}).get("buses", []):
		if bus.id == document.changed_subject:
			old_target = bus
	if target.is_empty() or old_target.is_empty() or target.effects.size() != old_target.effects.size():
		return false
	var volume: float = target.volumeDb
	var parameters: Array = []
	target.volumeDb = old_target.volumeDb
	for index in target.effects.size():
		var values: Dictionary = target.effects[index].parameters
		var original: Dictionary = old_target.effects[index].parameters
		if values.size() != original.size() or not values.has_all(original.keys()):
			return false
		parameters.append(target.effects[index].parameters.duplicate())
		target.effects[index].parameters = old_target.effects[index].parameters
	if current != previous:
		return false
	client.send_bus_volume(target.name, volume)
	for index in parameters.size():
		for key in parameters[index]:
			client.send_effect_parameter(target.name, index, key, parameters[index][key])
	return true

func _exit_tree() -> void:
	if document != null and document.changed.is_connected(_changed):
		document.changed.disconnect(_changed)
	if client != null:
		client.disconnect_from_game()
