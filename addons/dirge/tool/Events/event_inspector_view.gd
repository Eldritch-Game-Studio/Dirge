@tool
extends RefCounted

const Form = preload("res://addons/dirge/tool/Events/dom_form.gd")
const Edits = preload("res://addons/dirge/tool/Events/event_edits.gd")
const Model = preload("res://addons/dirge/model/Project/project_model.gd")
var state
var edits
var assets
var host
var form

func _init(editor_state, event_edits, asset_picker, document_host) -> void:
	state = editor_state
	edits = event_edits
	assets = asset_picker
	host = document_host
	form = Form.new(host, "event-inspector-form")

func render() -> void:
	form.begin()
	var content = ""
	if state.event.is_empty(): content = form.note("No event.")
	elif not state.primary_instrument.is_empty(): content = render_instrument(state.primary_instrument)
	elif not state.primary_marker.is_empty(): content = render_marker(state.primary_marker)
	elif state.selected_instruments.size() > 1:
		content = form.note("%d instruments selected." % state.selected_instruments.size())
		var first = state.find_instrument(state.selected_instruments[0])
		content += form.section("Apply to all", form.number("Volume", first.get("volumeDb", 0), func(value): edits.set_all("volumeDb", value), "dB") + form.number("Pitch", first.get("pitch", 1), func(value): edits.set_all("pitch", maxf(0.01, value)), "x"))
	elif state.selected_markers.size() > 1: content = form.note("%d markers selected." % state.selected_markers.size())
	elif not state.find_track(state.selected_track).is_empty(): content = render_track(state.find_track(state.selected_track))
	else: content = form.note("Select an instrument, marker or track.")
	form.commit(content)

func number(object: Dictionary, key: String, label: String, minimum: float = -INF, maximum: float = INF, integer: bool = false, unit: String = "") -> String:
	return form.number(label, object.get(key, 0.0), func(value): edits.set_value(label, object, key, int(clampf(value, minimum, maximum)) if integer else clampf(value, minimum, maximum)), unit, integer)

func enum_field(object: Dictionary, key: String, label: String, options: Array) -> String:
	return form.enum_choice(label, object.get(key, options[0]), options, func(value): edits.set_value(label, object, key, value))

func bool_field(object: Dictionary, key: String, label: String) -> String:
	return form.toggle(label, object.get(key, false), func(value): edits.set_value(label, object, key, value))

func text_field(object: Dictionary, key: String, label: String) -> String:
	return form.text(label, object.get(key, ""), func(value): edits.set_value(label, object, key, str(value).strip_edges()))

func reference_field(object: Dictionary, key: String, label: String, options: Array, name_key: String = "name") -> String:
	var names: Array = []
	var selected = -1
	for index in options.size():
		names.append(("G: " if state.is_global_parameter(options[index].id) else "") + str(options[index].get(name_key, "(unnamed)")))
		if options[index].id == object.get(key, Model.NONE): selected = index
	return form.choice(label, names, selected, func(index): edits.set_value(label, object, key, options[index].id))

func asset_field(object: Dictionary, key: String = "asset") -> String:
	return form.row("Asset", form.text_input(object.get(key, ""), func(value): edits.set_value("Asset", object, key, str(value).strip_edges()), "res://…", "field grow") + form.button("Browse", func(): assets.pick(object.get(key, ""), func(path): edits.set_value("Asset", object, key, path))))

func render_instrument(instrument: Dictionary) -> String:
	var kind: String = instrument.get("$type", "")
	var content = form.div(form.span(kind, "type " + kind.to_lower()) + form.text_input(instrument.get("name", ""), func(value): edits.set_value("Rename instrument", instrument, "name", str(value).strip_edges()), "name", "field name"), "head")
	content += render_type(instrument)
	var placement: Dictionary = instrument.placement
	var placement_fields = reference_field(placement, "parameter", "Sheet", [{"id": Model.NONE, "name": "Timeline"}] + state.parameters())
	placement_fields += number(placement, "start", "Start", -INF, INF, false, "s" if placement.parameter == Model.NONE else "")
	placement_fields += number(placement, "length", "Length", 0, INF, false, "s (0 = natural)" if placement.parameter == Model.NONE else "")
	placement_fields += number(instrument, "syncGroup", "Sync group", 0, INF, true)
	content += form.section("Placement", placement_fields, "grid")
	var common = number(instrument, "volumeDb", "Volume", -INF, INF, false, "dB")
	common += number(instrument, "pitch", "Pitch", 0.01, INF, false, "x")
	common += number(instrument, "volumeRandomDb", "Volume random", 0, INF, false, "dB")
	common += number(instrument, "pitchRandom", "Pitch random", 0)
	common += number(instrument, "startOffset", "Start offset", 0, INF, false, "s")
	common += number(instrument, "fadeIn", "Fade in", 0, INF, false, "s")
	common += number(instrument, "fadeOut", "Fade out", 0, INF, false, "s")
	common += bool_field(instrument, "loop", "Loop")
	common += bool_field(instrument, "async", "Async")
	common += enum_field(instrument, "cutMode", "Cut mode", ["Cut", "FadeOut"])
	common += number(instrument, "polyphony", "Polyphony", 0, INF, true)
	common += number(instrument, "triggerDelay", "Trigger delay", 0, INF, false, "s")
	common += number(instrument, "probability", "Probability", 0, 1)
	content += form.section("Instrument", common, "grid")
	content += render_conditions(instrument.conditions)
	return content

func render_type(instrument: Dictionary) -> String:
	var kind: String = instrument.get("$type", "")
	var content = ""
	match kind:
		"Clip": content = asset_field(instrument)
		"Multi":
			content = enum_field(instrument, "mode", "Mode", ["Random", "Shuffle", "Sequential"])
			content += number(instrument, "avoidRepeat", "Avoid repeat", 0, INF, true)
			content += render_entries(instrument.entries)
		"Scatterer":
			content = number(instrument, "minInterval", "Min interval", 0, INF, false, "s")
			content += number(instrument, "maxInterval", "Max interval", 0, INF, false, "s")
			content += number(instrument, "minScatterDistance", "Min distance", 0, INF, false, "m")
			content += number(instrument, "maxScatterDistance", "Max distance", 0, INF, false, "m")
			content += enum_field(instrument, "mode", "Mode", ["Random", "Shuffle", "Sequential"])
			content += render_entries(instrument.entries)
		"Event":
			var events: Array = state.project.get("events", []).filter(func(event): return event.id != state.event_id)
			content = reference_field(instrument, "event", "Event", events, "path")
		"Programmer":
			content = text_field(instrument, "key", "Key")
			content += form.note("Resolved through audio tables, then the ProgrammerSound callback.")
		"Command":
			content = enum_field(instrument, "action", "Action", ["StartEvent", "StopEvent", "SetParameter"])
			content += reference_field(instrument, "targetEvent", "Target event", state.project.get("events", []), "path")
			if instrument.get("action", "") == "SetParameter":
				var parameters: Array = state.parameters()
				var target = state.find_event(instrument.get("targetEvent", Model.NONE))
				if not target.is_empty() and target.id != state.event_id:
					for parameter in target.get("localParameters", []):
						parameters.append({"id": parameter.id, "name": target.get("path", "") + "/" + parameter.get("name", "")})
				content += reference_field(instrument, "targetParameter", "Parameter", parameters)
				content += number(instrument, "value", "Value")
		"Snapshot":
			content = reference_field(instrument, "snapshot", "Snapshot", state.project.get("snapshots", []))
			content += form.note("Started while the region is active; stopped on exit.")
		"Silence": content = form.note("Plays nothing for its length; useful in sequence gaps and parameter sheets.")
	return form.section(kind, content)

func render_entries(entries: Array) -> String:
	var content = ""
	for entry in entries:
		var fields = asset_field(entry)
		fields += number(entry, "weight", "Weight", 0)
		fields += form.button("Remove", func(): edits.list_remove("Remove entry", entries, entry), "btn small danger")
		content += form.div(fields, "item inline")
	content = form.div(content, "list")
	content += form.div(form.button("+ Entry", func(): edits.list_add("Add entry", entries, {"asset": "", "weight": 1.0})) + form.button("+ Browse…", func(): assets.pick("", func(path): edits.list_add("Add entry", entries, {"asset": path, "weight": 1.0}))), "actions")
	return content

func render_conditions(conditions: Array) -> String:
	var content = ""
	for condition in conditions:
		var fields = reference_field(condition, "parameter", "Parameter", state.parameters())
		fields += number(condition, "minimum", "Min")
		fields += number(condition, "maximum", "Max")
		fields += form.button("Remove", func(): edits.list_remove("Remove condition", conditions, condition), "btn small danger")
		content += form.div(fields, "item inline")
	content = form.div(content, "list")
	content += form.div(form.button("+ Condition", func():
		var parameters = state.parameters()
		var parameter: Dictionary = parameters[0] if not parameters.is_empty() else {}
		edits.list_add("Add condition", conditions, {"parameter": parameter.get("id", Model.NONE), "minimum": parameter.get("minimum", 0.0), "maximum": parameter.get("maximum", 1.0)})), "actions")
	return form.section("Conditions", content)

func render_marker(marker: Dictionary) -> String:
	var kind: String = marker.get("$type", "")
	var content = form.div(form.span(kind, "type marker") + form.text_input(marker.get("name", ""), func(value): edits.set_value("Rename marker", marker, "name", str(value).strip_edges()), "name", "field name"), "head")
	var fields = number(marker, "position", "Position", 0, INF, false, "s")
	if Edits.is_region(marker): fields += number(marker, "length", "Length", 0.01, INF, false, "s")
	fields += number(marker, "probability", "Probability", 0, 1)
	if kind in ["Transition", "TransitionRegion"]:
		fields += reference_field(marker, "destination", "Destination", state.event.markers.filter(func(item): return item.get("$type", "") not in ["Transition", "TransitionRegion"]))
		fields += enum_field(marker, "quantization", "Quantization", ["None", "Beat", "Bar", "Marker"])
		fields += number(marker, "transitionLength", "Transition length", 0, INF, false, "s")
	elif kind == "Loop": fields += form.note("Timeline wraps to the region start when it reaches the end and the conditions pass.")
	elif kind == "SustainPoint": fields += form.note("Timeline holds here until KeyOff.")
	elif kind == "Destination": fields += form.note("Jump target for transitions; raises a marker callback when reached.")
	elif kind == "Magnet": fields += form.note("Seeks landing inside the region snap to its start.")
	content += form.section("Marker", fields, "grid")
	if kind in ["Transition", "TransitionRegion"]: content += render_transition(marker.transitionInstruments)
	content += render_conditions(marker.conditions)
	return content

func render_transition(instruments: Array) -> String:
	var content = ""
	for instrument in instruments:
		var fields = form.span(instrument.get("$type", ""), "type small")
		fields += text_field(instrument, "name", "Name")
		fields += render_type(instrument)
		fields += number(instrument.placement, "start", "Start", 0, INF, false, "s")
		fields += number(instrument.placement, "length", "Length", 0, INF, false, "s")
		fields += number(instrument, "volumeDb", "Volume", -INF, INF, false, "dB")
		fields += form.button("Remove", func(): edits.list_remove("Remove transition instrument", instruments, instrument), "btn small danger")
		content += form.div(fields, "item")
	content += form.div(form.choice_button(["Clip", "Multi", "Silence"], -1, func(index):
		var kind: String = ["Clip", "Multi", "Silence"][index]
		var instrument = Edits.create_instrument(kind, Model.NONE, 0, 1 if kind == "Silence" else 0)
		instrument.name = "Transition " + kind.to_lower()
		edits.list_add("Add transition instrument", instruments, instrument), "instrument…"), "actions")
	return form.section("Transition timeline", content)

func render_track(track: Dictionary) -> String:
	var content = form.div(form.span("Track", "type track") + form.text_input(track.get("name", ""), func(value): edits.set_value("Rename track", track, "name", str(value).strip_edges()), "name", "field name"), "head")
	var fields = number(track, "volumeDb", "Volume", -INF, INF, false, "dB")
	fields += form.note("%d instruments" % track.instruments.size())
	var actions = form.button("Remove track", func(): edits.remove_track(track), "btn danger")
	var index = state.event.tracks.find(track)
	if index > 0: actions += form.button("Move up", func(): edits.reorder_track(track, index - 1))
	if index + 1 < state.event.tracks.size(): actions += form.button("Move down", func(): edits.reorder_track(track, index + 1))
	return content + form.section("Track", fields + form.div(actions, "actions"))
