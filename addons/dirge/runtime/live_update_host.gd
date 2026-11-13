@tool
extends Node

const Protocol = preload("res://addons/dirge/runtime/live_protocol.gd")
const ProjectJson = preload("res://addons/dirge/model/Serialization/project_json.gd")
signal request_handled(message: Dictionary)
signal client_connected
signal client_disconnected
var server
var port: int = 0
var is_listening: bool:
	get: return _listener.is_listening()
var has_client: bool:
	get: return _client != null
var _listener = TCPServer.new()
var _reader = Protocol.FrameReader.new()
var _client: StreamPeerTCP
var _stats_timer: float = 0.0
var _instances_timer: float = 0.0

func _init(owner_server = null) -> void:
	server = owner_server
	name = "LiveUpdateHost"

func _enter_tree() -> void:
	if server == null:
		server = get_parent()

func listen(listen_port: int) -> Error:
	close()
	if listen_port <= 0 or listen_port > 65535:
		return ERR_INVALID_PARAMETER
	var error = _listener.listen(listen_port, "127.0.0.1")
	if error != OK:
		push_error("Dirge live: cannot listen on 127.0.0.1:%s: %s." % [listen_port, error])
		return error
	port = listen_port
	return OK

func close() -> void:
	_drop_client()
	_listener.stop()

func _exit_tree() -> void:
	close()

func _process(delta: float) -> void:
	if server == null:
		return
	if _listener.is_connection_available():
		_accept(_listener.take_connection())
	if _client == null:
		return
	_client.poll()
	if _client.get_status() != StreamPeerTCP.STATUS_CONNECTED:
		_drop_client()
		return
	_receive()
	if _client == null:
		return
	_stats_timer += delta
	if _stats_timer >= 0.1:
		_stats_timer = 0.0
		_send_stats()
	_instances_timer += delta
	if _client != null and _instances_timer >= 0.2:
		_instances_timer = 0.0
		_send_instances()

func _accept(peer: StreamPeerTCP) -> void:
	_drop_client()
	peer.set_no_delay(true)
	_client = peer
	_reader.reset()
	_stats_timer = 0.1
	_instances_timer = 0.2
	_send({"type": "hello", "version": server.Version, "projectName": server.System.project.get("name", "") if server.IsLoaded else ""})
	client_connected.emit()

func _drop_client() -> void:
	if _client == null:
		return
	_client.disconnect_from_host()
	_client = null
	_reader.reset()
	client_disconnected.emit()

func _receive() -> void:
	var available = _client.get_available_bytes()
	if available <= 0:
		return
	if available + _reader.pending > Protocol.MAX_FRAME_BYTES:
		_drop_client()
		return
	var result = _client.get_partial_data(available)
	if result[0] != OK or not _reader.append(result[1]):
		_drop_client()
		return
	while _client != null:
		var frame = _reader.read_frame()
		if frame == null:
			break
		var message = Protocol.decode(frame)
		if message.is_empty():
			_error("malformed message: invalid JSON, message type or protocol version.")
			continue
		_handle(message)

func _handle(message: Dictionary) -> void:
	match message.type:
		"project":
			if not message.get("json", null) is String:
				_error("project needs {json}.")
			else:
				var parser = JSON.new()
				if parser.parse(message.json) != OK or not parser.data is Dictionary:
					_error("malformed project JSON.")
				else:
					var project = ProjectJson.deserialize(message.json)
					if project.is_empty():
						_error("invalid project.")
					else:
						server.HotReload(project)
		"mixer-set": _handle_mixer_set(message)
		"start":
			var path = str(message.get("path", ""))
			_send({"type": "started", "handle": server.StartEvent(path), "path": path})
			_instances_timer = 0.2
		"stop":
			server.StopEvent(int(message.get("handle", 0)), bool(message.get("immediate", false)))
			_instances_timer = 0.2
		"set-param":
			var handle = int(message.handle) if message.get("handle") != null else 0
			if not _is_number(message.get("value", null)):
				_error("set-param needs a numeric value.")
			elif handle > 0:
				server.SetParameter(handle, str(message.get("name", "")), message.value)
			else:
				server.SetGlobalParameter(str(message.get("name", "")), message.value)
		"snapshot":
			var snapshot_name = str(message.get("name", ""))
			if message.get("start", true):
				if server.IsLoaded and not server.System.mixer.has_snapshot(snapshot_name):
					_error("unknown snapshot '%s'." % snapshot_name)
				else:
					server.StartSnapshot(snapshot_name)
			else:
				server.StopSnapshot(snapshot_name)
		_: _error("'%s' is not a request the game handles." % message.type)
	request_handled.emit(message)

func _handle_mixer_set(message: Dictionary) -> void:
	if not server.IsLoaded:
		_error("no project loaded.")
		return
	var bus = str(message.get("bus", ""))
	if _is_number(message.get("volumeDb", null)):
		if not server.LiveSetBusVolume(bus, message.volumeDb):
			_error("unknown bus '%s'." % bus)
	elif _is_number(message.get("effect", null)) and message.get("name", null) is String and _is_number(message.get("value", null)):
		if not server.LiveSetEffectParameter(bus, int(message.effect), message.name, message.value):
			_error("unknown effect %s on bus '%s'." % [message.effect, bus])
	else:
		_error("mixer-set needs {bus, volumeDb} or {bus, effect, name, value}.")

static func _is_number(value) -> bool:
	return value is int or value is float

func _send_stats() -> void:
	var stats: Dictionary = server.GetStats()
	var levels: Array = []
	for bus in server.Driver.bus_names:
		var index = server.Driver.bus_index(bus)
		levels.append({"name": bus, "left": AudioServer.get_bus_peak_volume_left_db(index, 0), "right": AudioServer.get_bus_peak_volume_right_db(index, 0)})
	_send({"type": "stats", "time": stats.time, "instances": stats.instances,
		"realVoices": stats.real_voices, "updateMs": stats.update_ms, "buses": levels,
		"momentaryLufs": stats.momentary_lufs, "shortTermLufs": stats.short_term_lufs,
		"integratedLufs": stats.integrated_lufs, "truePeakDbtp": stats.true_peak_dbtp})

func _send_instances() -> void:
	var instances: Array = []
	for handle in server.Handles:
		var instance = server.Handles[handle]
		instances.append({"handle": handle, "event": instance.description.path,
			"state": instance.state, "position": instance.timeline_position, "virtual": instance.is_virtual})
	_send({"type": "instances", "instances": instances})

func _error(message: String) -> void:
	_send({"type": "error", "message": message})

func _send(message: Dictionary) -> void:
	if _client != null and _client.put_data(Protocol.encode(message)) != OK:
		_drop_client()
