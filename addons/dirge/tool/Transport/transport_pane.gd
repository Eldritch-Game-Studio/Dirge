@tool
extends HSplitContainer
const Docs = preload("res://addons/dirge/tool/Core/tool_documents.gd")
const Panner = preload("res://addons/dirge/tool/Transport/preview_panner.gd")
const Commands = preload("res://addons/dirge/tool/Core/tool_commands.gd")
signal preview_changed(emitter: Dictionary, listener: Dictionary)
var document: RefCounted
var server: Node
var host: Control
var panner: Control
var selected_event: String = ""
var loop_enabled: bool = false
var _local_values: Dictionary = {}
var _sliders: Dictionary = {}
var _parameter_signatures: Dictionary = {}
var _tracked: Dictionary = {}
var _instance_rows: Dictionary = {}
var _user_stopped: Dictionary = {}
var _restart: Array[String] = []
var _active_slider: String = ""
var _dropdown: String = ""
var _refresh_in: float = 0.0

func setup(value: RefCounted, audition: Node = null) -> void:
	document = value
	server = audition
	host = Docs.create("transport.html")
	add_child(Docs.slot(host, "DocumentSlot", Vector2(360, 0)))
	panner = Panner.new()
	panner.size_flags_horizontal = Control.SIZE_FILL
	panner.changed.connect(_panner_changed)
	add_child(panner)
	host.connect("clicked", _clicked)
	host.connect("dom_event_detail", _dom_event)
	for type in ["pointerdown", "pointermove", "pointerup", "keydown"]:
		host.call("listen", type)
	document.changed.connect(_document_changed)
	if server != null:
		server.Loaded.connect(_loaded)
	set_event("")

func _exit_tree() -> void:
	if document != null and document.changed.is_connected(_document_changed): document.changed.disconnect(_document_changed)
	if is_instance_valid(server) and server.Loaded.is_connected(_loaded): server.Loaded.disconnect(_loaded)

func _loaded(_system: Variant = null) -> void:
	_tracked.clear()
	_user_stopped.clear()
	_restart.clear()
	_panner_changed()

func _document_changed() -> void:
	if not document.dirty and document.changed_scope == document.ChangeScope.ALL:
		_local_values.clear()
	set_event(selected_event)

func _event(id: String) -> Dictionary:
	for event in document.project.events:
		if event.id == id: return event
	return {}

func set_event(id: String) -> void:
	selected_event = id
	var event = _event(id)
	if event.is_empty(): selected_event = ""
	host.call("set_text", "#event-name", "No event selected" if event.is_empty() else event.path + ("  ·  3D" if event.macros.is3D else ""))
	Docs.class_state(host, "#event-name", "none", event.is_empty())
	Docs.disabled(host, "#play", event.is_empty())
	Docs.disabled(host, "#stop", event.is_empty())
	panner.set_distances(event.get("macros", {}).get("minDistance", 1.0), event.get("macros", {}).get("maxDistance", 20.0))
	host.call("set_text", "#local-heading", "Event parameters" if event.is_empty() else "Event parameters · " + str(event.path).get_file())
	_parameter_list("global", document.project.globalParameters, "No global parameters")
	_parameter_list("local", event.get("localParameters", []), "Select an event" if event.is_empty() else "No local parameters")
	_refresh_values()

func play() -> int:
	return _play_event(selected_event)

func _play_event(id: String) -> int:
	if server == null or not server.IsLoaded: return 0
	var event = _event(id)
	if event.is_empty(): return 0
	var handle: int = server.StartEvent(event.path)
	if handle == 0: return 0
	_tracked[handle] = id
	var instance = server.GetInstance(handle)
	if instance != null:
		for name in _local_values.get(id, {}): instance.set_parameter(name, _local_values[id][name], true)
		if event.macros.is3D: instance.set_3d_attributes(panner.emitter)
	return handle

func stop_selected() -> void:
	if server == null: return
	for handle in server.Handles:
		var instance = server.GetInstance(handle)
		if instance != null and instance.description.id == selected_event: stop_instance(handle)

func stop_instance(handle: int) -> void:
	_user_stopped[handle] = true
	server.StopEvent(handle)

func stop_all() -> void:
	_restart.clear()
	if server == null: return
	for handle in server.Handles: _user_stopped[handle] = true
	server.StopAllEvents()

func set_loop(value: bool) -> void:
	loop_enabled = value
	Docs.class_state(host, "#loop", "on", value)
	if not value: _restart.clear()

func handle(command: int) -> bool:
	if command == Commands.Command.PLAY_STOP and not selected_event.is_empty():
		var playing = false
		if server != null and server.IsLoaded:
			for instance in server.System.instances:
				if instance.description.id == selected_event and instance.state != "Stopped": playing = true; break
		if playing: stop_selected()
		else: play()
		return true
	if command == Commands.Command.ESCAPE and not _dropdown.is_empty():
		_hide_dropdown()
		return true
	return false

func _process(delta: float) -> void:
	if host == null or server == null: return
	for handle in _tracked.keys():
		if not server.IsEventPlaying(handle):
			if loop_enabled and not _user_stopped.has(handle): _restart.append(_tracked[handle])
			_tracked.erase(handle)
			_user_stopped.erase(handle)
	var restart = _restart.duplicate()
	_restart.clear()
	for id in restart: _play_event(id)
	_refresh_in -= delta
	if _refresh_in > 0.0: return
	_refresh_in = 0.1
	_refresh_instances()
	_refresh_values()
	var stats = server.GetStats()
	host.call("set_text", "#ro-instances", str(stats.get("instances", 0)))
	host.call("set_text", "#ro-voices", str(stats.get("real_voices", 0)))
	host.call("set_text", "#ro-ms", "%.1f ms" % stats.get("update_ms", 0.0))

func _refresh_instances() -> void:
	var desired: Dictionary = {}
	if server.IsLoaded:
		for handle in server.Handles:
			var instance = server.GetInstance(handle)
			if instance == null: continue
			desired[handle] = true
			var id = "instance-" + str(handle)
			if not _instance_rows.has(handle):
				host.call("append_html", "#instance-list", '<div id="%s" class="inst"><p class="iname">%s</p><p class="istate"></p><p class="ipos"></p><button id="stop-instance-%d" class="istop">Stop</button></div>' % [id, Docs.escape(instance.description.path), handle])
			host.call("set_text", "#" + id + " .istate", str(instance.state) + (" (virtual)" if instance.is_virtual else ""))
			Docs.class_state(host, "#" + id + " .istate", "playing", instance.state in ["Playing", "Sustaining"])
			host.call("set_text", "#" + id + " .ipos", "%.2f s" % instance.timeline_position)
	for handle in _instance_rows:
		if not desired.has(handle): host.call("remove_element", "#instance-" + str(handle))
	_instance_rows = desired
	if desired.is_empty() and not host.call("has_element", "#instances-empty"):
		host.call("append_html", "#instance-list", '<p id="instances-empty" class="empty">Nothing playing — select an event and press Play</p>')
	elif not desired.is_empty() and host.call("has_element", "#instances-empty"):
		host.call("remove_element", "#instances-empty")

func _parameter_list(group: String, definitions: Array, empty: String) -> void:
	var signature = JSON.stringify(definitions)
	if _parameter_signatures.get(group, "") == signature: return
	_parameter_signatures[group] = signature
	if _active_slider.begins_with(group + "-"): _active_slider = ""
	if _dropdown.begins_with(group + "-"): _hide_dropdown()
	for id in _sliders.keys():
		if id.begins_with(group + "-"): _sliders.erase(id)
	var html = ""
	for definition in definitions:
		var id = group + "-" + str(definition.id)
		_sliders[id] = {"definition": definition.duplicate(true), "global": group == "global", "shown": NAN}
		var builtin = definition.builtIn != "None"
		html += '<div id="%s" class="param"><p class="name%s">%s</p>' % [id, " builtin" if builtin else "", Docs.escape(definition.name)]
		if definition.kind == "Labeled":
			html += '<button id="dd-%s" class="dd">—</button>' % id
		else:
			html += '<div id="slider-%s" class="slider%s"><div class="fill" style="transform:scaleX(0)"></div></div><p class="pval"></p>' % [id, " readonly" if builtin else ""]
		html += '</div>'
	if definitions.is_empty(): html = '<p class="empty">%s</p>' % Docs.escape(empty)
	host.call("set_inner_html", "#" + group + "-params", html)

func _current_value(slider: Dictionary) -> float:
	var definition: Dictionary = slider.definition
	if server != null and server.IsLoaded:
		if slider.global: return server.GetGlobalParameter(definition.name)
		for instance in server.System.instances:
			if instance.description.id == selected_event and instance.state != "Stopped": return instance.get_parameter(definition.name)
	return _local_values.get(selected_event, {}).get(definition.name, definition.default) if not slider.global else definition.default

func _refresh_values() -> void:
	for id in _sliders:
		if id != _active_slider: _show_value(id, _current_value(_sliders[id]))

func _show_value(id: String, value: float) -> void:
	var slider = _sliders[id]
	if slider.shown == value: return
	slider.shown = value
	var definition = slider.definition
	if definition.kind == "Labeled":
		var index = roundi(value)
		host.call("set_text", "#dd-" + id, str(definition.labels[index]) if index >= 0 and index < definition.labels.size() else "—")
	else:
		var span: float = definition.maximum - definition.minimum
		var fraction = 0.0 if span <= 0 else clampf((value - definition.minimum) / span, 0, 1)
		host.call("set_style", "#slider-" + id + " .fill", "transform", "scaleX(%.6f)" % fraction)
		host.call("set_text", "#" + id + " .pval", "%.0f" % value if definition.kind == "Discrete" else "%.2f" % value)

func set_parameter(name: String, value: float) -> bool:
	for id in _sliders:
		if _sliders[id].definition.name == name and _sliders[id].definition.builtIn == "None":
			_set_parameter(id, value)
			return true
	return false

func _set_parameter(id: String, value: float) -> void:
	var slider = _sliders[id]
	var definition: Dictionary = slider.definition
	if definition.builtIn != "None": return
	if definition.kind == "Labeled": value = clampf(roundf(value), 0.0, maxf(0, definition.labels.size() - 1))
	else: value = clampf(value, definition.minimum, definition.maximum)
	if definition.kind == "Discrete": value = roundf(value)
	if slider.global:
		if server != null and server.IsLoaded: server.SetGlobalParameter(definition.name, value)
	else:
		if not _local_values.has(selected_event): _local_values[selected_event] = {}
		_local_values[selected_event][definition.name] = value
		if server != null and server.IsLoaded:
			for instance in server.System.instances:
				if instance.description.id == selected_event: instance.set_parameter(definition.name, value)
	_show_value(id, value)

func _clicked(id: String) -> void:
	match id:
		"play": play()
		"stop": stop_selected()
		"stop-all": stop_all()
		"loop": set_loop(not loop_enabled)
	if id.begins_with("stop-instance-"): stop_instance(int(id.trim_prefix("stop-instance-")))
	elif id.begins_with("dd-") and _sliders.has(id.trim_prefix("dd-")):
		var key = id.trim_prefix("dd-")
		if _dropdown == key: _hide_dropdown()
		elif _sliders[key].definition.builtIn == "None": _show_dropdown(key)
	elif id.begins_with("label-") and not _dropdown.is_empty():
		_set_parameter(_dropdown, float(id.trim_prefix("label-")))
		_hide_dropdown()

func _dom_event(type: String, id: String, details: Dictionary) -> void:
	if type == "pointerdown":
		if not id.begins_with("dd-") and not id.begins_with("label-"): _hide_dropdown()
		if id.begins_with("slider-"):
			var key = id.trim_prefix("slider-")
			if _sliders.has(key) and _sliders[key].definition.builtIn == "None":
				_active_slider = key
				host.call("set_pointer_capture", "#" + id, details.get("pointer_id", 0))
				_apply_pointer(float(details.get("client_x", 0)))
	elif type == "pointermove" and not _active_slider.is_empty():
		if int(details.get("buttons", 0)) & 1: _apply_pointer(float(details.get("client_x", 0)))
		else: _active_slider = ""
	elif type == "pointerup" and not _active_slider.is_empty():
		_apply_pointer(float(details.get("client_x", 0)))
		var selector = "#slider-" + _active_slider
		var pointer = details.get("pointer_id", 0)
		if host.call("has_pointer_capture", selector, pointer): host.call("release_pointer_capture", selector, pointer)
		_active_slider = ""

func _apply_pointer(x: float) -> void:
	var rect: Rect2 = host.call("get_element_rect", "#slider-" + _active_slider)
	if rect.size.x <= 0: return
	var definition = _sliders[_active_slider].definition
	var fraction = clampf((x - rect.position.x) / rect.size.x, 0, 1)
	_set_parameter(_active_slider, lerpf(definition.minimum, definition.maximum, fraction))

func _show_dropdown(id: String) -> void:
	_dropdown = id
	var html = ""
	var labels = _sliders[id].definition.labels
	var current = roundi(_current_value(_sliders[id]))
	for index in labels.size():
		html += '<button id="label-%d" class="%s">%s</button>' % [index, "current" if index == current else "", Docs.escape(labels[index])]
	if labels.is_empty(): html = '<p class="empty">No labels</p>'
	host.call("set_inner_html", "#dd-popup", html)
	var rect: Rect2 = host.call("get_element_rect", "#dd-" + id)
	host.call("set_style", "#dd-popup", "left", "%dpx" % rect.position.x)
	host.call("set_style", "#dd-popup", "top", "%dpx" % rect.end.y)
	host.call("set_style", "#dd-popup", "min-width", "%dpx" % rect.size.x)
	Docs.hidden(host, "#dd-popup", false)

func _hide_dropdown() -> void:
	_dropdown = ""
	Docs.hidden(host, "#dd-popup", true)

func _panner_changed() -> void:
	if server != null and server.IsLoaded:
		server.System.set_listener(0, panner.listener)
		for instance in server.System.instances:
			var event = _event(instance.description.id)
			if event.get("macros", {}).get("is3D", false): instance.set_3d_attributes(panner.emitter)
	preview_changed.emit(panner.emitter, panner.listener)
