@tool
extends HSplitContainer
const Documents = preload("res://addons/dirge/tool/Core/tool_documents.gd")
const Dom = preload("res://addons/dirge/tool/Mixer/dom.gd")
const Feed = preload("res://addons/dirge/tool/Profiler/profiler_feed.gd")
const Graph = preload("res://addons/dirge/tool/Profiler/update_graph.gd")
const Timeline = preload("res://addons/dirge/tool/Profiler/recording_canvas.gd")
const Recorder = preload("res://addons/dirge/model/Profiling/recorder.gd")
const Replayer = preload("res://addons/dirge/model/Profiling/replayer.gd")
const RecordingJson = preload("res://addons/dirge/model/Profiling/recording_json.gd")
const Client = preload("res://addons/dirge/runtime/live_update_client.gd")
signal event_selected(id: String)
var server
var document
var host
var dom
var feed
var graph = Graph.new()
var timeline = Timeline.new()
var recording: Dictionary = {}
var compare_result: String = ""
var last_divergence = null
var _recorder
var _client
var _owned_client: bool = false
var _clock: float = 0.0
var _bound: bool = false
var _rows: int = 0
var _bus_rows: int = 0
var _selected_handle: int = -1
var _recording_title: String = "No recording"
var _recording_path: String = "user://rec.json"
var _dialog: FileDialog
var is_recording: bool:
	get: return _recorder != null

func setup(audition_server, project_document = null) -> void:
	if is_instance_valid(server) and server.Loaded.is_connected(_on_loaded): server.Loaded.disconnect(_on_loaded)
	server = audition_server
	document = project_document
	if feed != null: feed.dispose()
	feed = Feed.new(server)
	if is_instance_valid(server): server.Loaded.connect(_on_loaded)
	if is_inside_tree(): _mount()

func _ready() -> void:
	_mount()
func _mount() -> void:
	if host != null: return
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	host = Documents.create("profiler.html")
	add_child(Documents.slot(host, "Document", Vector2(520, 0)))
	var canvases = VBoxContainer.new()
	canvases.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	canvases.size_flags_vertical = Control.SIZE_EXPAND_FILL
	canvases.custom_minimum_size = Vector2(300, 0)
	add_child(canvases)
	canvases.add_child(graph)
	canvases.add_child(timeline)
	timeline.bar_clicked.connect(func(path, _handle): _select_event(path))
	dom = Dom.new(host)
	host.clicked.connect(_on_click)

func _exit_tree() -> void:
	stop_recording()
	if feed != null: feed.dispose()
	if is_instance_valid(server) and server.Loaded.is_connected(_on_loaded): server.Loaded.disconnect(_on_loaded)
	if _owned_client and is_instance_valid(_client): _client.disconnect_from_game()

func set_live_client(client) -> void:
	if _owned_client and is_instance_valid(_client):
		_client.disconnect_from_game()
		_client.queue_free()
	_client = client
	_owned_client = false

func use_editor() -> void:
	if feed == null: feed = Feed.new(server)
	feed.use_editor()
	if _owned_client and is_instance_valid(_client):
		_client.disconnect_from_game()
		_client.queue_free()
		_client = null
		_owned_client = false
	graph.clear()
	_refresh_controls()

func connect_to_game(address: String = "127.0.0.1", port: int = 7447) -> Error:
	stop_recording()
	if not is_instance_valid(_client):
		_client = Client.new()
		add_child(_client)
		_owned_client = true
	var error = OK
	if not _client.is_connected_to_game or _client.host != address or _client.port != port:
		error = _client.connect_to_game(address, port)
	if feed == null: feed = Feed.new(server)
	feed.use_game(_client, address + ":" + str(port))
	graph.clear()
	_refresh_controls()
	return error

func _connect_input() -> void:
	var text = host.get_value("#host").strip_edges()
	var address = "127.0.0.1"
	var port = 7447
	var colon = text.rfind(":")
	if colon >= 0:
		if colon > 0: address = text.substr(0, colon).strip_edges()
		if text.substr(colon + 1).is_valid_int(): port = int(text.substr(colon + 1))
	elif not text.is_empty(): address = text
	connect_to_game(address, port)

func _on_loaded(_system) -> void:
	if _recorder != null: stop_recording()
	graph.clear()
	if is_instance_valid(server) and server.IsLoaded: graph.voice_capacity = server.System.max_real_voices

func _process(delta: float) -> void:
	if host == null or not host.has_element("#instance-rows"): return
	if not _bound:
		_bound = true
		_refresh_controls()
	if not is_visible_in_tree() or feed == null: return
	var wall = Time.get_ticks_usec() / 1000000.0
	if feed.poll(): graph.push(wall, feed.stats.get("update_ms", 0), feed.stats.get("real_voices", 0))
	graph.advance(wall)
	_clock += delta
	if _clock < 1.0 / 15.0: return
	_clock = 0.0
	_refresh_live()

static func format_loudness(value, suffix: String = "LUFS") -> String:
	if value == null or is_nan(float(value)): return "—"
	if is_inf(float(value)): return "silent"
	return "%.1f %s" % [float(value), suffix]

func _refresh_live() -> void:
	feed.poll_rows()
	var stats = feed.stats
	dom.text("#c-instances", stats.get("instances", 0))
	dom.text("#c-voices", stats.get("real_voices", 0))
	dom.text("#c-update", "%.1f ms" % stats.get("update_ms", 0))
	dom.text("#c-time", "%.1f s" % stats.get("time", 0))
	for mapping in [["c-lufs-m", "momentary_lufs"], ["c-lufs-s", "short_term_lufs"], ["c-lufs-i", "integrated_lufs"], ["c-peak", "true_peak_dbtp"]]:
		dom.text("#" + mapping[0], format_loudness(stats.get(mapping[1], NAN), "dBTP" if mapping[0] == "c-peak" else "LUFS"))
	dom.text("#source-status", feed.status)
	for index in range(feed.instances.size()):
		if index >= _rows: _append_instance_row(index)
		var data = feed.instances[index]
		var prefix = "#instance-%d" % index
		dom.hidden(prefix, false)
		dom.text(prefix + "-handle", data.get("handle", 0))
		dom.text(prefix + "-event", data.get("event", ""))
		dom.text(prefix + "-state", data.get("state", ""))
		dom.text(prefix + "-position", "%.2f s" % data.get("position", 0))
		dom.text(prefix + "-virtual", "virt" if data.get("virtual", false) else "—")
		dom.text(prefix + "-parameters", data.get("parameters", "—"))
		dom.toggle(prefix + "-state", "playing", data.get("state") in ["Playing", "Sustaining"])
		dom.toggle(prefix + "-state", "stopping", data.get("state") == "Stopping")
		dom.toggle(prefix, "selected", data.get("handle", -1) == _selected_handle)
	for index in range(feed.instances.size(), _rows): dom.hidden("#instance-%d" % index, true)
	dom.hidden("#instances-empty", not feed.instances.is_empty())
	for index in range(feed.buses.size()):
		if index >= _bus_rows: _append_bus_row(index)
		var data = feed.buses[index]
		var prefix = "#level-%d" % index
		dom.hidden(prefix, false)
		dom.text(prefix + "-name", data.get("name", data.get("bus", "")))
		var peak = maxf(float(data.get("left", -80)), float(data.get("right", -80)))
		dom.text(prefix + "-db", "-inf dB" if peak <= -60 else "%.1f dB" % (roundf(peak * 2) / 2))
		var fraction = clampf((peak + 60) / 60, 0, 1)
		dom.style(prefix + "-low", "transform", "scaleX(%s)" % String.num(snappedf(minf(fraction, 0.8) / 0.8, 0.001), 3))
		dom.style(prefix + "-mid", "transform", "scaleX(%s)" % String.num(snappedf(clampf((fraction - 0.8) / (59.0 / 60.0 - 0.8), 0, 1), 0.001), 3))
		dom.style(prefix + "-high", "transform", "scaleX(%s)" % String.num(snappedf(clampf((fraction - 59.0 / 60.0) * 60, 0, 1), 0.001), 3))
	for index in range(feed.buses.size(), _bus_rows): dom.hidden("#level-%d" % index, true)
	if _recorder != null: dom.text("#rec-status", "Recording… %d ticks" % _recorder.recording.get("ticks", []).size())

func _append_instance_row(index: int) -> void:
	var html = '<div id="instance-%d" class="row">' % index
	for field in ["handle", "event", "state", "position", "virtual", "parameters"]:
		var css = {"position": "pos", "virtual": "virt", "parameters": "params"}.get(field, field)
		html += '<p id="instance-%d-%s" class="c-%s"></p>' % [index, field, css]
	html += '<p class="c-act"><button id="instance-%d-stop" class="small">Stop</button></p></div>' % index
	host.append_html("#instance-rows", html)
	_rows += 1
func _append_bus_row(index: int) -> void:
	var html = '<div id="level-%d" class="bus"><div class="bus-head"><p id="level-%d-name" class="name"></p><p id="level-%d-db" class="db"></p></div><div class="meter horizontal">' % [index, index, index]
	for segment in ["low", "mid", "high"]: html += '<div id="level-%d-%s" class="fill %s" style="transform:scaleX(0)"></div>' % [index, segment, segment]
	host.append_html("#bus-rows", html + '</div></div>')
	_bus_rows += 1

func _select_event(path: String) -> void:
	if document == null: return
	for event in document.project.get("events", []):
		if event.get("path") == path:
			event_selected.emit(event.id)
			return
func _on_click(id: String) -> void:
	match id:
		"src-editor": use_editor()
		"src-game", "connect": _connect_input()
		"rec-start": start_recording()
		"rec-stop": stop_recording()
		"rec-save": _open_dialog(true)
		"rec-load": _open_dialog(false)
		"rec-replay": replay()
		_:
			if id.begins_with("instance-"):
				var parts = id.split("-")
				if parts.size() < 3: return
				var index = int(parts[1])
				if index >= feed.instances.size(): return
				var data = feed.instances[index]
				if parts[2] == "stop": feed.stop_instance(int(data.handle), false)
				elif parts[2] == "event":
					_selected_handle = int(data.handle)
					_select_event(str(data.event))

func start_recording() -> bool:
	if _recorder != null: return true
	if feed == null or feed.recordable_system == null:
		_status("Recording needs the editor source")
		return false
	var system = feed.recordable_system
	if system.recorder != null:
		_status("This system already has a recorder")
		return false
	if system.time != 0:
		var project = document.project if document != null else system.project
		server.Load(project)
		system = server.System
	_recorder = Recorder.new(system)
	_clear_compare()
	_refresh_controls()
	return true

func stop_recording() -> Dictionary:
	if _recorder == null: return {}
	recording = _recorder.stop()
	_recorder = null
	_recording_title = "Recording · %d ticks · %.2f s" % [recording.get("ticks", []).size(), duration(recording)]
	timeline.set_recording(recording, _recording_title)
	_refresh_controls()
	return recording

func save_recording(path: String) -> Error:
	if recording.is_empty(): return ERR_UNAVAILABLE
	var file = FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		_status("Save failed: " + error_string(FileAccess.get_open_error()))
		return FileAccess.get_open_error()
	file.store_string(RecordingJson.serialize(recording))
	file.close()
	_recording_path = path
	_recording_title = path + " · %d ticks" % recording.get("ticks", []).size()
	timeline.set_recording(recording, _recording_title)
	_refresh_controls()
	_status("Saved " + path)
	return OK

func load_recording(path: String) -> bool:
	if not FileAccess.file_exists(path):
		_status("Not found: " + path)
		return false
	var parsed = RecordingJson.deserialize(FileAccess.get_file_as_string(path))
	if parsed.is_empty() or not parsed.has("ticks"):
		_status("Load failed: invalid recording")
		return false
	stop_recording()
	recording = parsed
	_clear_compare()
	_recording_path = path
	_recording_title = path + " · %d ticks · %.2f s" % [recording.ticks.size(), duration(recording)]
	timeline.set_recording(recording, _recording_title)
	_refresh_controls()
	return true

func replay():
	if recording.is_empty(): return null
	var project = document.project if document != null else server.System.project if is_instance_valid(server) and server.IsLoaded else {}
	if project.is_empty():
		_status("Replay requires a project")
		return null
	var assets = server.Assets if is_instance_valid(server) else null
	var replayed = Replayer.replay(recording, project, null, assets)
	last_divergence = Replayer.compare(recording, replayed)
	if last_divergence == null:
		var commands = 0
		for tick in recording.get("ticks", []): commands += tick.get("commands", []).size()
		compare_result = "Compare: identical · %d ticks, %d commands" % [recording.get("ticks", []).size(), commands]
		timeline.set_divergence(NAN)
	else:
		compare_result = "Compare: diverged at tick %d #%d · expected %s · actual %s" % [last_divergence.tick, last_divergence.index, str(last_divergence.expected), str(last_divergence.actual)]
		var tick = int(last_divergence.tick)
		timeline.set_divergence(float(recording.ticks[tick].time) if tick >= 0 and tick < recording.ticks.size() else duration(recording))
	if _bound:
		dom.text("#rec-compare", compare_result)
		dom.toggle("#rec-compare", "diverged", last_divergence != null)
		dom.hidden("#rec-compare", false)
	return last_divergence

static func duration(value: Dictionary) -> float:
	var ticks = value.get("ticks", [])
	return float(ticks.back().get("time", 0)) + float(ticks.back().get("dt", 0)) if not ticks.is_empty() else 0.0
func _clear_compare() -> void:
	compare_result = ""
	last_divergence = null
	timeline.set_divergence(NAN)
	if _bound: dom.hidden("#rec-compare", true)
func _status(text: String) -> void:
	if _bound: dom.text("#rec-status", text)
func _refresh_controls() -> void:
	if not _bound: return
	var remote = feed != null and feed.remote
	dom.toggle("#src-editor", "selected", not remote)
	dom.toggle("#src-game", "selected", remote)
	dom.enabled("#rec-start", not is_recording and feed != null and feed.recordable_system != null)
	dom.enabled("#rec-stop", is_recording)
	dom.enabled("#rec-save", not recording.is_empty() and not is_recording)
	dom.enabled("#rec-load", not is_recording)
	dom.enabled("#rec-replay", not recording.is_empty() and not is_recording)
	dom.toggle("#rec-start", "active", is_recording)
	dom.value("#rec-path", _recording_path)
	_status("Recording…" if is_recording else "No recording · recording needs the editor source" if remote and recording.is_empty() else _recording_title)
func _open_dialog(save: bool) -> void:
	if _dialog == null:
		_dialog = FileDialog.new()
		_dialog.access = FileDialog.ACCESS_FILESYSTEM
		_dialog.filters = PackedStringArray(["*.json ; Dirge recording"])
		_dialog.size = Vector2i(820, 560)
		add_child(_dialog)
	for connection in _dialog.file_selected.get_connections(): _dialog.file_selected.disconnect(connection.callable)
	_dialog.file_mode = FileDialog.FILE_MODE_SAVE_FILE if save else FileDialog.FILE_MODE_OPEN_FILE
	_dialog.title = "Save recording" if save else "Load recording"
	_dialog.current_path = ProjectSettings.globalize_path(host.get_value("#rec-path"))
	_dialog.file_selected.connect(func(path):
		if save: save_recording(path)
		else: load_recording(path), CONNECT_ONE_SHOT)
	_dialog.popup_centered()
