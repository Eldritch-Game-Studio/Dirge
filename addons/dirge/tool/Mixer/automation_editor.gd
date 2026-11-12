@tool
extends RefCounted
const Ops = preload("res://addons/dirge/tool/Mixer/mixer_graph_ops.gd")
const Catalog = preload("res://addons/dirge/tool/Mixer/effect_catalog.gd")
const Dom = preload("res://addons/dirge/tool/Mixer/dom.gd")
var pane
var bus_id: String = ""
var _structure: String = ""
func _init(owner) -> void:
	pane = owner

func add_lane(target: Dictionary, parameter: String, points: Array = []) -> String:
	if Ops.find(pane._project().get("globalParameters", []), parameter).is_empty(): return ""
	var lane = {"id": Ops.new_id(), "target": target.duplicate(true), "parameter": parameter, "curve": {"points": points.duplicate(true)}}
	pane._edit("Add mixer automation", func(): pane._project().get_or_add("mixerAutomation", []).append(lane))
	return lane.id
func remove_lane(id: String) -> void:
	pane._edit("Remove mixer automation", func(): pane._project().get("mixerAutomation", []).erase(Ops.find(pane._project().get("mixerAutomation", []), id)))
func set_points(id: String, points: Array) -> void:
	if Ops.find(pane._project().get("mixerAutomation", []), id).is_empty(): return
	var sorted = points.duplicate(true)
	sorted.sort_custom(func(a, b): return float(a.x) < float(b.x))
	pane._edit("Mixer automation curve", func(): Ops.find(pane._project().mixerAutomation, id).curve = {"points": sorted})
func open(id: String) -> void:
	bus_id = id
	_structure = ""
	if not pane.host.has_element("#automation-popover"):
		pane.host.append_html("#mixer", '<div id="automation-popover" class="popover" hidden><div class="popover-head"><div id="automation-title" class="popover-title">Automation</div>' + pane._button("automation-add", "+ Lane", "tool", _target_picker) + pane._button("automation-close", "x", "tool", close) + '</div><div id="automation-body" class="popover-body"></div></div>')
	refresh()
	pane._position_popover("#automation-popover", 380)
func close() -> void:
	bus_id = ""
	if pane.dom != null and pane.host.has_element("#automation-popover"): pane.dom.hidden("#automation-popover", true)
func _target_picker() -> void:
	var items: Array = []
	for kind in ["BusVolumeDb", "BusPitch"]:
		var target = Ops.property(kind, bus_id)
		items.append({"label": kind, "action": func(): _parameter_picker(target)})
	var effects = pane._bus(bus_id).get("effects", [])
	for index in range(effects.size()):
		for parameter in Catalog.parameters(effects[index].typeName):
			if parameter.kind == "String": continue
			var target = Ops.property("EffectParameter", bus_id, index, parameter.name)
			items.append({"label": Catalog.display_name(effects[index]) + " / " + parameter.label, "action": func(): _parameter_picker(target)})
	for vca in pane._mixer().get("vcas", []):
		var target = Ops.property("VcaVolumeDb", vca.id)
		items.append({"label": "VCA / " + str(vca.name), "action": func(): _parameter_picker(target)})
	pane._popup("AUTOMATION TARGET", items)
func _parameter_picker(target: Dictionary) -> void:
	var items: Array = []
	for parameter in pane._project().get("globalParameters", []):
		var pid = str(parameter.id)
		var minimum = float(parameter.get("minimum", 0))
		var maximum = float(parameter.get("maximum", 1))
		items.append({"label": parameter.get("name", ""), "action": func(): add_lane(target, pid, [{"x": minimum, "y": 0.0, "shapeToNext": "Linear"}, {"x": maximum, "y": 0.0, "shapeToNext": "Linear"}])})
	pane._popup("GLOBAL PARAMETER", items)
func _write_point(id: String, index: int, key: String, value) -> void:
	var lane = Ops.find(pane._project().get("mixerAutomation", []), id)
	if lane.is_empty(): return
	var points = lane.get("curve", {}).get("points", []).duplicate(true)
	if index < 0 or index >= points.size(): return
	points[index][key] = value
	set_points(id, points)
func refresh() -> void:
	if bus_id.is_empty(): return
	if pane._bus(bus_id).is_empty():
		close()
		return
	var lanes: Array = []
	var signature = ""
	for lane in pane._project().get("mixerAutomation", []):
		if lane.get("target", {}).get("owner") == bus_id or lane.get("target", {}).get("kind") == "VcaVolumeDb":
			lanes.append(lane)
			signature += str(lane.id) + str(lane.get("curve", {}).get("points", []).size())
	if signature != _structure or not pane.host.has_element("#automation-content"):
		_structure = signature
		var html = '<div id="automation-content">'
		for lane in lanes:
			var id = str(lane.id)
			var parameter = Ops.find(pane._project().get("globalParameters", []), lane.get("parameter", ""))
			html += '<div class="lfo-row"><div class="field-label">%s → %s</div>' % [Dom.escape(parameter.get("name", "?")), Dom.escape(lane.target.get("name") if lane.target.get("name") != null else lane.target.get("kind", ""))]
			html += pane._button("automation-remove-" + id, "Remove lane", "tool", func(): remove_lane(id))
			var points = lane.get("curve", {}).get("points", [])
			for index in range(points.size()):
				var prefix = "point-" + id + "-" + str(index)
				html += '<div class="param-row">'
				for key in ["x", "y"]:
					html += pane._text_input(prefix + "-" + key, points[index].get(key, 0), func(v):
						if str(v).is_valid_float(): _write_point(id, index, key, float(v)))
				html += pane._button(prefix + "-shape", points[index].get("shapeToNext", "Linear"), "pill", func():
					var items: Array = []
					for shape in ["Linear", "Smooth", "Stepped"]: items.append({"label": shape, "action": func(): _write_point(id, index, "shapeToNext", shape)})
					pane._popup("CURVE SHAPE", items))
				html += pane._button(prefix + "-remove", "x", "fx-x", func():
					var next = Ops.find(pane._project().mixerAutomation, id).curve.points.duplicate(true)
					next.remove_at(index)
					set_points(id, next)) + '</div>'
			html += pane._button("point-add-" + id, "+ Point", "tool", func():
				var next = Ops.find(pane._project().mixerAutomation, id).curve.points.duplicate(true)
				next.append({"x": float(next.back().x) + 1 if not next.is_empty() else 0.0, "y": 0.0, "shapeToNext": "Linear"})
				set_points(id, next)) + '</div>'
		pane.host.set_inner_html("#automation-body", html + '</div>')
		pane.dom.forget()
	pane.dom.text("#automation-title", str(pane._bus(bus_id).name) + " / Automation")
	for lane in lanes:
		var points = lane.get("curve", {}).get("points", [])
		for index in range(points.size()):
			var prefix = "#point-" + str(lane.id) + "-" + str(index)
			pane.dom.value(prefix + "-x", points[index].get("x", 0))
			pane.dom.value(prefix + "-y", points[index].get("y", 0))
			pane.dom.text(prefix + "-shape", points[index].get("shapeToNext", "Linear"))
