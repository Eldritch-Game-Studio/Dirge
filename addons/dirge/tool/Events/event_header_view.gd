@tool
extends RefCounted

const Form = preload("res://addons/dirge/tool/Events/dom_form.gd")
const Model = preload("res://addons/dirge/model/Project/project_model.gd")
const QUANTIZATION = ["None", "Beat", "Bar", "Marker"]
signal sections_changed
var state
var edits
var host
var form
var playing: bool = false
var show_macros: bool = false
var show_parameters: bool = false
var show_tempo: bool = false
var play: Callable
var fit: Callable

func _init(editor_state, event_edits, document_host, play_toggle: Callable, zoom_fit: Callable) -> void:
	state = editor_state
	edits = event_edits
	host = document_host
	play = play_toggle
	fit = zoom_fit
	form = Form.new(host, "event-header-form")
	if Engine.is_editor_hint():
		var settings = EditorInterface.get_editor_settings()
		if settings.has_setting("dirge/header_sections"):
			var stored = str(settings.get_setting("dirge/header_sections"))
			show_macros = "macros" in stored
			show_parameters = "parameters" in stored
			show_tempo = "tempo" in stored

func authored_height() -> float:
	return 150.0 if show_macros or show_parameters or show_tempo else 29.0

func _toggle(section: String) -> void:
	match section:
		"macros": show_macros = not show_macros
		"parameters": show_parameters = not show_parameters
		"tempo": show_tempo = not show_tempo
	if Engine.is_editor_hint():
		var sections = []
		if show_macros: sections.append("macros")
		if show_parameters: sections.append("parameters")
		if show_tempo: sections.append("tempo")
		EditorInterface.get_editor_settings().set_setting("dirge/header_sections", ",".join(sections))
	render()
	sections_changed.emit()

func render() -> void:
	var event: Dictionary = state.event
	form.begin("toolbar")
	if event.is_empty():
		form.commit(form.button("Global parameters", func(): _toggle("parameters"), "toggle on" if show_parameters else "toggle"))
		form.begin()
		form.commit(_parameters(event, true) if show_parameters else form.note("No event selected."))
		return
	var toolbar = form.button("Stop" if playing else "Play", play, "btn accent" if playing else "btn")
	toolbar += form.button("Snap", func(): state.snap_to_grid = not state.snap_to_grid; state.notify_selection(), "toggle on" if state.snap_to_grid else "toggle")
	toolbar += form.button("Fit", fit)
	toolbar += form.span("Event", "label")
	var rename = func(value):
		var trimmed = str(value).strip_edges().trim_prefix("/").trim_suffix("/")
		if not trimmed.is_empty(): edits.set_value("Rename event", state.event, "path", trimmed)
	toolbar += form.text_input(event.get("path", ""), rename, "", "field path")
	toolbar += form.span("Bus", "label")
	var buses: Array = state.project.get("mixer", {}).get("buses", [])
	var names: Array = []
	var selected = -1
	for index in buses.size():
		names.append(buses[index].get("name", "Bus"))
		if buses[index].id == event.get("bus", Model.NONE): selected = index
	toolbar += form.choice_button(names, selected, func(index): edits.set_value("Route event", state.event, "bus", buses[index].id), "(master)")
	toolbar += form.button("Conductor", func(): edits.set_value("Conductor", state.event, "isConductor", not state.event.get("isConductor", false)), "toggle on" if event.get("isConductor", false) else "toggle")
	toolbar += form.button("Macros", func(): _toggle("macros"), "toggle on" if show_macros else "toggle")
	toolbar += form.button("Parameters", func(): _toggle("parameters"), "toggle on" if show_parameters else "toggle")
	toolbar += form.button("Tempo", func(): _toggle("tempo"), "toggle on" if show_tempo else "toggle")
	form.commit(toolbar)
	form.begin()
	var content = ""
	if show_macros: content += _macros(event.macros)
	if show_parameters:
		content += _parameters(event)
		content += _parameters(event, true)
	if show_tempo: content += _tempo(event)
	form.commit(content)

func _number(object: Dictionary, key: String, label: String, minimum: float = -INF, maximum: float = INF, integer: bool = false, unit: String = "") -> String:
	return form.number(label, object.get(key, 0.0), func(value): edits.set_value(label, object, key, int(clampf(value, minimum, maximum)) if integer else clampf(value, minimum, maximum)), unit, integer)

func _enum(object: Dictionary, key: String, label: String, options: Array) -> String:
	return form.enum_choice(label, object.get(key, options[0]), options, func(value): edits.set_value(label, object, key, value))

func _bool(object: Dictionary, key: String, label: String) -> String:
	return form.toggle(label, object.get(key, false), func(value): edits.set_value(label, object, key, value))

func _macros(macros: Dictionary) -> String:
	var content = _number(macros, "volumeDb", "Volume", -INF, INF, false, "dB")
	content += _number(macros, "pitch", "Pitch", 0.01, INF, false, "x")
	content += _number(macros, "priority", "Priority", -INF, INF, true)
	content += _number(macros, "maxInstances", "Max instances", 0, INF, true)
	content += _enum(macros, "stealing", "Stealing", ["Oldest", "Quietest", "Furthest", "Virtualize", "None"])
	content += _number(macros, "cooldown", "Cooldown", 0, INF, false, "s")
	content += _bool(macros, "persistent", "Persistent")
	content += _bool(macros, "is3D", "3D")
	content += _number(macros, "minDistance", "Min distance", 0, INF, false, "m")
	content += _number(macros, "maxDistance", "Max distance", 0, INF, false, "m (0 = never)")
	content += _enum(macros, "rolloff", "Rolloff", ["Inverse", "InverseSquare", "Logarithmic", "Disabled"])
	content += _bool(macros, "doppler", "Doppler")
	content += _number(macros, "dopplerScale", "Doppler scale", 0)
	content += _number(macros, "scheduleLookahead", "Lookahead", 0, INF, false, "s")
	content += _enum(macros, "startQuantization", "Start quantization", QUANTIZATION)
	return form.section("Macros", content, "grid")

func _parameters(event: Dictionary, global: bool = false) -> String:
	var content = ""
	var parameters: Array = state.project.get("globalParameters", []) if global else event.get("localParameters", [])
	for parameter in parameters:
		var fields = form.text("Name", parameter.get("name", ""), func(value):
			if not str(value).strip_edges().is_empty(): edits.set_value("Rename parameter", parameter, "name", str(value).strip_edges()))
		fields += _enum(parameter, "kind", "Kind", ["Continuous", "Discrete", "Labeled"])
		fields += _enum(parameter, "builtIn", "Built-in", ["None", "Distance", "DistanceNormalized", "Direction", "Elevation", "EventConeAngle", "EventOrientation", "Speed", "SpeedAbsolute"])
		fields += _number(parameter, "minimum", "Min")
		fields += _number(parameter, "maximum", "Max")
		fields += _number(parameter, "default", "Default")
		fields += _number(parameter, "seekSpeedUp", "Seek up", 0, INF, false, "/s")
		fields += _number(parameter, "seekSpeedDown", "Seek down", 0, INF, false, "/s")
		fields += _bool(parameter, "hold", "Hold")
		if parameter.get("kind", "") == "Labeled":
			fields += form.text("Labels", ", ".join(parameter.get("labels", [])), func(value):
				var labels: Array = []
				for part in str(value).split(","):
					if not part.strip_edges().is_empty(): labels.append(part.strip_edges())
				edits.set_value("Parameter labels", parameter, "labels", labels), "comma separated")
		fields += form.button("Remove", func(): edits.list_remove("Remove parameter", parameters, parameter), "btn danger")
		content += form.div(fields, "item grid")
	content = form.div(content, "list")
	content += form.div(form.button("+ Parameter", func(): add_parameter(global)), "actions")
	return form.section("Global parameters" if global else "Local parameters", content)

func add_parameter(global: bool = false) -> void:
	var parameters: Array = state.project.globalParameters if global else state.event.localParameters
	var names: Array = []
	for parameter in parameters: names.append(parameter.name)
	var name = "Param"
	var number = 2
	while name in names:
		name = "Param%d" % number
		number += 1
	edits.list_add("Add parameter", parameters, Model.create("ParameterDefinition", {"name": name}))

func _tempo(event: Dictionary) -> String:
	var content = ""
	for marker in event.get("tempo", []):
		var fields = form.number("At", marker.position, func(value): edits.set_tempo_position(marker, maxf(0, value)), "s")
		fields += _number(marker, "bpm", "BPM", 1)
		fields += _number(marker, "beatsPerBar", "Beats/bar", 1, INF, true)
		fields += form.button("Remove", func(): edits.list_remove("Remove tempo marker", event.tempo, marker), "btn danger")
		content += form.div(fields, "item inline")
	if event.tempo.is_empty(): content += form.note("No tempo: ruler shows seconds only; snapping uses 0.1 s.")
	content += form.div(form.button("+ Tempo marker", func(): edits.add_tempo(state.cursor)), "actions")
	return form.section("Tempo markers", content)
