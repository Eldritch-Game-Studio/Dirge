@tool
extends RefCounted

const Form = preload("res://addons/dirge/tool/Events/dom_form.gd")
const Model = preload("res://addons/dirge/model/Project/project_model.gd")
var state
var edits
var curve
var host
var form

func _init(editor_state, event_edits, curve_canvas, document_host) -> void:
	state = editor_state
	edits = event_edits
	curve = curve_canvas
	host = document_host
	form = Form.new(host, "event-automation-form")

func render() -> void:
	form.begin()
	var event: Dictionary = state.event
	if event.is_empty():
		form.commit(form.note("No event."))
		return
	var instrument: Dictionary = state.primary_instrument
	var content = render_lanes(event, instrument)
	content += render_modulators(event, false)
	if not instrument.is_empty(): content += render_modulators(instrument, true)
	form.commit(content)

func parameter_options() -> Array:
	var names: Array = []
	var ids: Array = []
	for parameter in state.parameters():
		names.append(("G: " if state.is_global_parameter(parameter.id) else "") + parameter.get("name", ""))
		ids.append(parameter.id)
	return [names, ids]

func default_parameter() -> String:
	if state.sheet != Model.NONE: return state.sheet
	var parameters = state.parameters()
	return parameters[0].id if not parameters.is_empty() else Model.NONE

func target_options() -> Array:
	var event: Dictionary = state.event
	var instrument: Dictionary = state.primary_instrument
	var targets = [Model.property_ref("EventVolumeDb", event.id), Model.property_ref("EventPitch", event.id)]
	if not instrument.is_empty():
		targets.append(Model.property_ref("InstrumentVolumeDb", instrument.id))
		targets.append(Model.property_ref("InstrumentPitch", instrument.id))
	var bus = state.find_bus(event.get("bus", Model.NONE))
	if bus.is_empty():
		for candidate in state.project.get("mixer", {}).get("buses", []):
			if candidate.get("parent", Model.NONE) == Model.NONE:
				bus = candidate
				break
	for index in bus.get("effects", []).size():
		for key in bus.effects[index].get("parameters", {}):
			targets.append(Model.property_ref("EffectParameter", bus.id, index, key))
	return targets

func add_lane(target: Dictionary) -> Dictionary:
	var parameter = default_parameter()
	var definition = state.find_parameter(parameter)
	var neutral = 1.0 if str(target.kind).ends_with("Pitch") else 0.0
	if target.kind == "EffectParameter":
		var bus = state.find_bus(target.owner)
		neutral = bus.effects[target.effectIndex].parameters.get(target.name, 0.0)
	var lane = Model.create("AutomationLane", {"target": target, "parameter": parameter, "curve": {"points": [[definition.get("minimum", 0.0), neutral, "Linear"], [definition.get("maximum", 1.0), neutral, "Linear"]]}})
	edits.list_add("Add automation lane", state.event.automation, lane)
	state.select_lane(lane.id)
	return lane

func render_lanes(event: Dictionary, instrument: Dictionary) -> String:
	var content = ""
	for lane in event.get("automation", []):
		var instrument_lane = str(lane.target.kind).begins_with("Instrument")
		if instrument_lane and (instrument.is_empty() or lane.target.owner != instrument.id): continue
		var fields = form.button(curve.target_label(lane.target), func(): state.select_lane(lane.id), "btn lane-pick on" if state.selected_lane == lane.id else "btn lane-pick")
		fields += form.span("by", "label short")
		var options = parameter_options()
		fields += form.choice_button(options[0], options[1].find(lane.parameter), func(index): edits.set_value("Lane parameter", lane, "parameter", options[1][index]), "(parameter)")
		fields += form.span("%d points" % lane.curve.points.size(), "muted")
		fields += form.button("Remove", func():
			edits.list_remove("Remove automation lane", state.event.automation, lane)
			if state.selected_lane == lane.id: state.select_lane(Model.NONE), "btn small danger")
		content += form.div(fields, "item inline lane")
	var targets = target_options()
	var names: Array = []
	for target in targets: names.append(curve.target_label(target))
	content += form.div(form.span("Add lane", "label short") + form.choice_button(names, -1, func(index): add_lane(targets[index]), "target…"), "actions")
	return form.section("Automation" if instrument.is_empty() else "Automation · " + instrument.get("name", ""), content)

func number(object: Dictionary, key: String, label: String, minimum: float = -INF, maximum: float = INF, unit: String = "") -> String:
	return form.number(label, object.get(key, 0), func(value): edits.set_value(label, object, key, clampf(value, minimum, maximum)), unit)

func add_modulator(owner: Dictionary, instrument: bool, kind: String) -> Dictionary:
	var prefix = "Instrument" if instrument else "Event"
	var target = Model.property_ref(prefix + ("Pitch" if kind == "Autopitch" else "VolumeDb"), owner.id)
	var values = {"$type": kind, "target": target, "amount": 1.0 if kind == "Autopitch" else 0.0 if kind == "Ahdsr" else 3.0}
	if kind == "Ahdsr": values.envelope = {"attack": 0.1, "hold": 0.0, "decay": 0.0, "sustain": 1.0, "release": 0.5}
	if kind == "Autopitch":
		values.parameter = default_parameter()
		values.referenceValue = state.find_parameter(values.parameter).get("default", 0.0)
	var modulator = Model.create("Modulator", values)
	edits.list_add("Add modulator", owner.modulators, modulator)
	return modulator

func render_modulators(owner: Dictionary, instrument: bool) -> String:
	var content = ""
	for modulator in owner.get("modulators", []):
		var kind: String = modulator.get("$type", "")
		var fields = form.span(kind, "type small")
		var prefix = "Instrument" if instrument else "Event"
		if kind != "Autopitch":
			fields += form.choice("Target", ["Volume", "Pitch"], 1 if str(modulator.target.kind).ends_with("Pitch") else 0, func(index): edits.set_value("Modulator target", modulator, "target", Model.property_ref(prefix + ("Pitch" if index == 1 else "VolumeDb"), owner.id)))
		fields += number(modulator, "amount", "Amount")
		match kind:
			"Ahdsr":
				var envelope: Dictionary = modulator.envelope
				fields += number(envelope, "attack", "Attack", 0, INF, "s")
				fields += number(envelope, "hold", "Hold", 0, INF, "s")
				fields += number(envelope, "decay", "Decay", 0, INF, "s")
				fields += number(envelope, "sustain", "Sustain", 0, 1)
				fields += number(envelope, "release", "Release", 0, INF, "s")
			"Lfo":
				fields += form.enum_choice("Shape", modulator.get("shape", "Sine"), ["Sine", "Square", "Triangle", "SawUp", "SawDown", "Noise"], func(value): edits.set_value("LFO shape", modulator, "shape", value))
				fields += number(modulator, "rateHz", "Rate (beats)" if modulator.get("tempoSync", false) else "Rate", 0, INF, "" if modulator.get("tempoSync", false) else "Hz")
				fields += number(modulator, "phase", "Phase")
				fields += form.toggle("Tempo sync", modulator.get("tempoSync", false), func(value): edits.set_value("LFO tempo sync", modulator, "tempoSync", value))
			"Autopitch":
				var options = parameter_options()
				fields += form.choice("Parameter", options[0], options[1].find(modulator.get("parameter", Model.NONE)), func(index): edits.set_value("Autopitch parameter", modulator, "parameter", options[1][index]))
				fields += number(modulator, "referenceValue", "Reference")
				fields += number(modulator, "pitchAtMinimum", "Pitch at min", 0.01, INF, "x")
				fields += number(modulator, "pitchAtMaximum", "Pitch at max", 0.01, INF, "x")
		fields += form.button("Remove", func(): edits.list_remove("Remove modulator", owner.modulators, modulator), "btn danger")
		content += form.div(fields, "item grid mod-" + kind.to_lower())
	content += form.div(form.span("Add", "label short") + form.choice_button(["Random", "AHDSR", "LFO", "Autopitch"], -1, func(index): add_modulator(owner, instrument, ["Random", "Ahdsr", "Lfo", "Autopitch"][index]), "modulator…"), "actions")
	return form.section("Instrument modulators" if instrument else "Event modulators", content)
