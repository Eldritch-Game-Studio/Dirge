@tool
extends Node

const Protocol = preload("res://addons/dirge/runtime/live_protocol.gd")
const ProjectJson = preload("res://addons/dirge/model/Serialization/project_json.gd")
signal hello(project_name: String)
signal stats(message: Dictionary)
signal instances(values: Array)
signal started(handle: int, path: String)
signal error_received(message: String)
signal disconnected(reason: String)
signal message_received(message: Dictionary)
@export var connect_timeout: float = 5.0
var state: String = "Disconnected"
var host: String = "127.0.0.1"
var port: int = Protocol.DEFAULT_PORT
var hello_info: Dictionary = {}
var last_stats: Dictionary = {}
var last_instances: Array = []
var is_connected_to_game: bool:
	get: return state == "Connected" and not hello_info.is_empty()
var _peer: StreamPeerTCP
var _reader = Protocol.FrameReader.new()
var _connect_elapsed: float = 0.0

func connect_to_game(remote_host: String = "127.0.0.1", remote_port: int = Protocol.DEFAULT_PORT) -> Error:
	disconnect_from_game()
	host = remote_host
	port = remote_port
	var peer = StreamPeerTCP.new()
	var error = peer.connect_to_host(host, port)
	if error != OK:
		disconnected.emit("connect to %s:%s failed: %s" % [host, port, error])
		return error
	peer.set_no_delay(true)
	_peer = peer
	state = "Connecting"
	_connect_elapsed = 0.0
	_reader.reset()
	return OK

func disconnect_from_game() -> void:
	if _peer != null:
		_peer.disconnect_from_host()
	_peer = null
	hello_info = {}
	state = "Disconnected"
	_reader.reset()

func _exit_tree() -> void:
	disconnect_from_game()

func _process(delta: float) -> void:
	if _peer == null:
		return
	_peer.poll()
	match _peer.get_status():
		StreamPeerTCP.STATUS_CONNECTING:
			_connect_elapsed += delta
			if _connect_elapsed > connect_timeout:
				_drop("connect to %s:%s timed out" % [host, port])
			return
		StreamPeerTCP.STATUS_NONE, StreamPeerTCP.STATUS_ERROR:
			_drop("connect to %s:%s refused" % [host, port] if state == "Connecting" else "connection closed")
			return
	state = "Connected"
	var available = _peer.get_available_bytes()
	if available <= 0:
		return
	if available + _reader.pending > Protocol.MAX_FRAME_BYTES:
		_drop("frame too large")
		return
	var result = _peer.get_partial_data(available)
	if result[0] != OK:
		_drop("read failed: %s" % result[0])
		return
	if not _reader.append(result[1]):
		_drop("frame too large")
		return
	while _peer != null:
		var frame = _reader.read_frame()
		if frame == null:
			break
		_dispatch(frame)

func _dispatch(frame: PackedByteArray) -> void:
	var message = Protocol.decode(frame)
	if message.is_empty():
		push_warning("Dirge live: dropped malformed message.")
		return
	match message.type:
		"hello":
			hello_info = message
			hello.emit(str(message.get("projectName", "")))
		"stats":
			last_stats = message
			stats.emit(message)
		"instances":
			last_instances.clear()
			if message.get("instances", []) is Array:
				last_instances.append_array(message.get("instances", []))
			instances.emit(last_instances)
		"started": started.emit(int(message.get("handle", 0)), str(message.get("path", "")))
		"error": error_received.emit(str(message.get("message", "")))
	message_received.emit(message)

func _drop(reason: String) -> void:
	disconnect_from_game()
	disconnected.emit(reason)

func send(message: Dictionary) -> bool:
	if _peer == null or state != "Connected":
		return false
	var error = _peer.put_data(Protocol.encode(message))
	if error != OK:
		_drop("write failed: %s" % error)
		return false
	return true

func send_project(project: Dictionary) -> bool:
	return send_project_json(ProjectJson.serialize(project, false))

func send_project_json(json: String) -> bool:
	return send({"type": "project", "json": json})

func send_bus_volume(bus: String, volume_db: float) -> bool:
	return send({"type": "mixer-set", "bus": bus, "volumeDb": volume_db})

func send_effect_parameter(bus: String, effect_index: int, parameter: String, value: float) -> bool:
	return send({"type": "mixer-set", "bus": bus, "effect": effect_index, "name": parameter, "value": value})

func send_start(path: String) -> bool:
	return send({"type": "start", "path": path})

func send_stop(handle: int, immediate: bool = false) -> bool:
	return send({"type": "stop", "handle": handle, "immediate": immediate})

func send_parameter(parameter: String, value: float, handle: int = 0) -> bool:
	var message = {"type": "set-param", "name": parameter, "value": value}
	if handle != 0:
		message["handle"] = handle
	return send(message)

func send_snapshot(snapshot_name: String, start: bool = true) -> bool:
	return send({"type": "snapshot", "name": snapshot_name, "start": start})
