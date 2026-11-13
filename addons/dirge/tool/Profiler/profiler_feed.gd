@tool
extends RefCounted
var server
var client
var remote: bool = false
var stats: Dictionary = {}
var instances: Array = []
var buses: Array = []
var _handles: Dictionary = {}
var _next_handle: int = 1
var _system
var _stats_dirty: bool = false
var _endpoint: String = ""
var status: String:
	get:
		if remote:
			if not is_instance_valid(client): return "Game · no connection"
			return "Game · " + str(client.hello_info.get("projectName", "")) + " @ " + _endpoint if client.is_connected_to_game else "Game · " + client.state + " " + _endpoint
		return "Editor audition" if recordable_system != null else "Editor audition · no project"
var recordable_system:
	get: return server.System if not remote and is_instance_valid(server) and server.IsLoaded else null

func _init(audition_server = null) -> void:
	server = audition_server

func use_editor() -> void:
	_detach_client()
	remote = false
	instances.clear()
	buses.clear()

func use_game(live_client, endpoint: String) -> void:
	_detach_client()
	client = live_client
	remote = true
	_endpoint = endpoint
	client.stats.connect(_on_stats)
	_stats_dirty = not client.last_stats.is_empty()
	instances.clear()
	buses.clear()

func dispose() -> void:
	_detach_client()
	_handles.clear()

func _detach_client() -> void:
	if is_instance_valid(client) and client.stats.is_connected(_on_stats): client.stats.disconnect(_on_stats)
	client = null
func _on_stats(_stats: Dictionary) -> void:
	_stats_dirty = true

func poll() -> bool:
	if remote:
		if not is_instance_valid(client) or not _stats_dirty: return false
		_stats_dirty = false
		var wire = client.last_stats
		stats = {"time": wire.get("time", 0), "instances": wire.get("instances", 0), "real_voices": wire.get("realVoices", 0), "update_ms": wire.get("updateMs", 0), "momentary_lufs": wire.get("momentaryLufs", NAN), "short_term_lufs": wire.get("shortTermLufs", NAN), "integrated_lufs": wire.get("integratedLufs", NAN), "true_peak_dbtp": wire.get("truePeakDbtp", NAN)}
		buses = wire.get("buses", [])
		return true
	if recordable_system == null:
		stats = {}
		return false
	stats = server.GetStats()
	return true

func poll_rows() -> void:
	instances.clear()
	if remote:
		if is_instance_valid(client) and client.is_connected_to_game: instances.assign(client.last_instances)
		return
	buses.clear()
	var system = recordable_system
	if system == null:
		_handles.clear()
		return
	if system != _system:
		_system = system
		_handles.clear()
	for instance in _handles.keys():
		if instance.state == "Stopped" or not system.instances.has(instance): _handles.erase(instance)
	for instance in system.instances:
		if instance.state == "Stopped": continue
		if not _handles.has(instance):
			_handles[instance] = _next_handle
			_next_handle += 1
		var parameters = PackedStringArray()
		for definition in instance.description.parameters:
			parameters.append("%s=%.2f" % [definition.get("name", ""), instance.get_parameter(definition.get("name", ""))])
		instances.append({"handle": _handles[instance], "event": instance.description.path, "state": instance.state, "position": instance.timeline_position, "virtual": instance.is_virtual, "parameters": "  ".join(parameters)})
	instances.sort_custom(func(a, b): return a.handle < b.handle)
	for bus in system.project.get("mixer", {}).get("buses", []):
		var index = AudioServer.get_bus_index(bus.get("name", ""))
		buses.append({"name": bus.get("name", ""), "left": AudioServer.get_bus_peak_volume_left_db(index, 0) if index >= 0 else -80.0, "right": AudioServer.get_bus_peak_volume_right_db(index, 0) if index >= 0 else -80.0})

func stop_instance(handle: int, immediate: bool = false) -> void:
	if remote:
		if is_instance_valid(client) and client.is_connected_to_game: client.send_stop(handle, immediate)
		return
	for instance in _handles:
		if _handles[instance] == handle:
			instance.stop("Immediate" if immediate else "AllowFadeout")
			return
