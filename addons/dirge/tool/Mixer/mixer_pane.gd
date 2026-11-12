@tool
extends Control
## Retains mixer.html and its CSS. All authored edits use the shared document transaction.
const Ops = preload("res://addons/dirge/tool/Mixer/mixer_graph_ops.gd")
const Catalog = preload("res://addons/dirge/tool/Mixer/effect_catalog.gd")
const Dom = preload("res://addons/dirge/tool/Mixer/dom.gd")
const Documents = preload("res://addons/dirge/tool/Core/tool_documents.gd")
signal selection_changed(kind: String, id: String)
var document
var server
var host
var dom
var selected_bus: String = ""
var selected_effect: String = ""
var editing_snapshot: String = ""
var _selected_kind: String = ""
var _selected_id: String = ""
var _rows: Dictionary = {}
var _vca_rows: Dictionary = {}
var _snapshot_rows: Dictionary = {}
var _clicks: Dictionary = {}
var _changes: Dictionary = {}
var _sliders: Dictionary = {}
var _drag: Dictionary = {}
var _popup_actions: Array = []
var _popup_open: bool = false
var _popover_bus: String = ""
var _popover_effect: String = ""
var _popover_structure: String = ""
var _lfo_bus: String = ""
var _lfo_structure: String = ""
var automation = preload("res://addons/dirge/tool/Mixer/automation_editor.gd").new(self)
var _pending: bool = true
var _bound: bool = false
var _snapshot_instance
var _snapshot_intensity: float = 0.0
var _snapshot_system
var _meter_clock: float = 0.0
var _suppress_click: bool = false
var _last_press_id: String = ""
var _last_press_time: float = -INF
var _bus_order: Array = []

func setup(project_document, audition_server = null) -> void:
	if document != null and document.changed.is_connected(_on_changed): document.changed.disconnect(_on_changed)
	document = project_document
	server = audition_server
	document.changed.connect(_on_changed)
	_pending = true
	if is_inside_tree(): _mount()

func _ready() -> void:
	_mount()

func _mount() -> void:
	if host != null or document == null: return
	clip_contents = true
	host = Documents.create("mixer.html")
	var slot = Documents.slot(host, "MixerDocument")
	add_child(slot)
	slot.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	dom = Dom.new(host)
	host.dom_event_detail.connect(_on_dom_event)
	for event in ["change", "keydown", "blur", "pointerdown", "pointermove", "pointerup", "pointercancel", "wheel"]: host.listen(event)
	host.cancel.connect(_escape)

func _exit_tree() -> void:
	_stop_snapshot()
	if document != null and document.changed.is_connected(_on_changed): document.changed.disconnect(_on_changed)

func _on_changed() -> void:
	if document.changed_scope & 7: _pending = true

func _process(delta: float) -> void:
	if host == null or document == null or not host.has_element("#bus-strips"): return
	if not _bound:
		_bound = true
		_clicks["bus-add"] = func(): add_bus(selected_bus)
		_clicks["vca-add"] = add_vca
		_clicks["snap-add"] = add_snapshot
		_clicks["snap-done"] = func(): select_snapshot("")
		_clicks["snap-capture"] = capture_snapshot
		_clicks["popover-close"] = func(): _close_popover()
		_clicks["lfo-popover-close"] = func(): _close_lfo()
		_clicks["lfo-add"] = func(): add_lfo(_lfo_bus)
		_sliders["snap-intensity"] = {"min": 0.0, "max": 1.0, "step": 0.01, "reset": 0.0, "vertical": false, "write": set_snapshot_intensity, "value": 0.0}
		for key in ["attack", "hold", "decay", "sustain", "release"]:
			_changes["snap-" + key] = func(value): set_snapshot_envelope(key, float(value))
	if _pending:
		_pending = false
		_refresh()
	_tick_snapshot()
	if is_visible_in_tree(): _meters(delta)

func _project() -> Dictionary:
	return document.project if document != null else {}
func _mixer() -> Dictionary:
	return _project().get("mixer", {})
func _bus(id: String) -> Dictionary:
	return Ops.find_bus(_mixer(), id)
func _snapshot() -> Dictionary:
	return Ops.find(_project().get("snapshots", []), editing_snapshot)
func _effect(bus_id: String, effect_id: String) -> Dictionary:
	return Ops.find(_bus(bus_id).get("effects", []), effect_id)
func _edit(label: String, edit: Callable) -> void:
	if document == null: return
	var scope = 11 if label == "Delete bus" else 3
	if not _drag.is_empty() and _drag.kind == "slider":
		document.transact_continuous(_drag.id, label, edit, scope, selected_bus)
	else:
		document.transact(label, edit, scope, selected_bus)
func _select(kind: String, id: String) -> void:
	_selected_kind = kind
	_selected_id = id
	selection_changed.emit(kind, id)
	_pending = true

func add_bus(parent: String = "") -> String:
	var master = Ops.master(_mixer())
	if master.is_empty(): return ""
	if _bus(parent).is_empty(): parent = master.id
	var id = Ops.new_id()
	var bus = {"id": id, "name": Ops.unique_name(_mixer().get("buses", []), "Bus"), "parent": parent, "volumeDb": 0.0, "mute": false, "solo": false, "bypassEffects": false, "effects": [], "faderIndex": Ops.AFTER_ALL, "vcas": [], "maxVoices": 0}
	_edit("Add bus", func(): _mixer().buses.append(bus))
	selected_bus = id
	_select("Bus", id)
	return id

func delete_bus(id: String) -> void:
	if _bus(id).is_empty() or _bus(id) == Ops.master(_mixer()): return
	_edit("Delete bus", func(): Ops.delete_bus(_project(), id))
	if selected_bus == id: selected_bus = ""
func set_bus_parent(id: String, parent: String) -> void:
	var bus = _bus(id)
	if bus.is_empty() or bus == Ops.master(_mixer()) or _bus(parent).is_empty() or Ops.is_self_or_descendant(_mixer(), parent, id): return
	_edit("Route bus", func(): _bus(id).parent = parent)
func move_bus(id: String, to: int) -> void:
	_edit("Reorder buses", func(): Ops.move_bus(_mixer(), id, to))
func set_bus_property(id: String, key: String, value) -> void:
	if _bus(id).is_empty(): return
	if key != "volumeDb" and _bus(id).get(key) == value: return
	if key == "volumeDb":
		_write_property(Ops.property("BusVolumeDb", id), clampf(float(value), -80, 12), func(v): _bus(id).volumeDb = v)
	else:
		_edit("Bus " + key, func(): _bus(id)[key] = value)
func rename_bus(id: String, value: String) -> void:
	value = value.strip_edges()
	if value.is_empty() or _bus(id).is_empty() or _bus(id) == Ops.master(_mixer()): return
	_edit("Rename bus", func(): _bus(id).name = value)

func add_effect(bus_id: String, type: String) -> String:
	return add_effect_definition(bus_id, Catalog.create(type))
func add_effect_definition(bus_id: String, effect: Dictionary) -> String:
	if _bus(bus_id).is_empty(): return ""
	_edit("Add effect", func(): Ops.insert_effect(_project(), bus_id, effect, _bus(bus_id).get("effects", []).size()))
	return effect.id
func add_ducking(bus_id: String, source: String) -> String:
	return add_effect_definition(bus_id, Catalog.ducking_preset(source))
func remove_effect(bus_id: String, effect_id: String) -> void:
	var index = _bus(bus_id).get("effects", []).find(_effect(bus_id, effect_id))
	if index >= 0: _edit("Remove effect", func(): Ops.remove_effect(_project(), bus_id, index))
func move_chain_row(bus_id: String, from: int, to: int) -> void:
	_edit("Move mixer chain row", func(): Ops.move_chain_row(_project(), bus_id, from, to))
func set_effect_parameter(bus_id: String, effect_id: String, key: String, value) -> void:
	var effect = _effect(bus_id, effect_id)
	if effect.is_empty(): return
	if value is String:
		if effect.get("stringParameters", {}).get(key) == value: return
		_edit("Effect " + key, func(): _effect(bus_id, effect_id).get_or_add("stringParameters", {})[key] = value)
	else:
		var index = _bus(bus_id).effects.find(effect)
		_write_property(Ops.property("EffectParameter", bus_id, index, key), float(value), func(v): _effect(bus_id, effect_id).get_or_add("parameters", {})[key] = v)

func add_vca() -> String:
	var id = Ops.new_id()
	var vca = {"id": id, "name": Ops.unique_name(_mixer().get("vcas", []), "VCA"), "volumeDb": 0.0}
	_edit("Add VCA", func(): _mixer().get_or_add("vcas", []).append(vca))
	_select("Vca", id)
	return id
func delete_vca(id: String) -> void:
	_edit("Remove VCA", func(): Ops.delete_vca(_project(), id))
func set_vca_property(id: String, key: String, value) -> void:
	if key == "name" and str(value).strip_edges().is_empty(): return
	if Ops.find(_mixer().get("vcas", []), id).get(key) == value: return
	if Ops.find(_mixer().get("vcas", []), id).is_empty(): return
	_edit("VCA " + key, func(): Ops.find(_mixer().vcas, id)[key] = value)
func toggle_vca_assignment(bus_id: String, vca_id: String) -> void:
	if _bus(bus_id).is_empty(): return
	_edit("Assign VCA", func():
		var assignments = _bus(bus_id).get_or_add("vcas", [])
		if assignments.has(vca_id): assignments.erase(vca_id)
		else: assignments.append(vca_id))

func add_snapshot() -> String:
	var id = Ops.new_id()
	var snapshot = {"id": id, "name": Ops.unique_name(_project().get("snapshots", []), "Snapshot"), "priority": 0, "mode": "Blending", "intensity": {"attack": 0.0, "hold": 0.0, "decay": 0.0, "sustain": 1.0, "release": 0.0}, "values": []}
	_edit("Add snapshot", func(): _project().get_or_add("snapshots", []).append(snapshot))
	select_snapshot(id)
	return id
func select_snapshot(id: String) -> void:
	if editing_snapshot == id: return
	_stop_snapshot()
	editing_snapshot = id
	_select("Snapshot", id)
func delete_snapshot(id: String) -> void:
	if editing_snapshot == id: select_snapshot("")
	_edit("Remove snapshot", func(): _project().get("snapshots", []).erase(Ops.find(_project().get("snapshots", []), id)))
func set_snapshot_property(id: String, key: String, value) -> void:
	if Ops.find(_project().get("snapshots", []), id).is_empty(): return
	if key == "name" and str(value).strip_edges().is_empty(): return
	if Ops.find(_project().get("snapshots", []), id).get(key) == value: return
	_edit("Snapshot " + key, func(): Ops.find(_project().snapshots, id)[key] = value)
func set_snapshot_envelope(key: String, value: float) -> void:
	if _snapshot().is_empty(): return
	value = clampf(value, 0, 1) if key == "sustain" else maxf(0, value)
	if _snapshot().get("intensity", {}).get(key) == value: return
	_edit("Snapshot " + key, func(): _snapshot().get_or_add("intensity", {})[key] = value)
func capture_snapshot() -> void:
	if _snapshot().is_empty(): return
	_edit("Capture snapshot", func():
		for bus in _mixer().get("buses", []):
			var target = Ops.property("BusVolumeDb", bus.id)
			if Ops.find_value(_snapshot(), target).is_empty(): Ops.set_value(_snapshot(), target, bus.get("volumeDb", 0)))
func unscope(target: Dictionary) -> void:
	if not _snapshot().is_empty(): _edit("Unscope snapshot value", func(): Ops.unscope(_snapshot(), target))
func _write_property(target: Dictionary, value: float, authored: Callable) -> void:
	if _snapshot().is_empty(): _edit("Mixer property", func(): authored.call(value))
	else: _edit("Snapshot property", func(): Ops.set_value(_snapshot(), target, value))
func _shown_value(target: Dictionary, authored: float) -> float:
	return float(Ops.find_value(_snapshot(), target).get("value", authored))
func set_snapshot_intensity(value: float) -> void:
	_snapshot_intensity = clampf(value, 0, 1)
	if dom != null: dom.text("#snap-intensity-text", "%d %%" % roundi(_snapshot_intensity * 100))
func _stop_snapshot() -> void:
	if _snapshot_instance != null: _snapshot_instance.stop()
	_snapshot_instance = null
	_snapshot_intensity = 0.0
	_snapshot_system = null
func _tick_snapshot() -> void:
	if not is_instance_valid(server) or not server.IsLoaded or _snapshot().is_empty(): return
	if _snapshot_intensity <= 0:
		if _snapshot_instance != null: _snapshot_instance.stop()
		_snapshot_instance = null
		return
	if _snapshot_system != server.System:
		_snapshot_instance = null
		_snapshot_system = server.System
	if _snapshot_instance == null or not _snapshot_instance.is_active:
		_snapshot_instance = server.System.mixer.start_snapshot(_snapshot().name)
	if _snapshot_instance != null: _snapshot_instance.intensity = _snapshot_intensity

func add_lfo(bus_id: String) -> String:
	if _bus(bus_id).is_empty(): return ""
	var id = Ops.new_id()
	var lfo = {"$type": "Lfo", "id": id, "target": Ops.property("BusVolumeDb", bus_id), "amount": 3.0, "rateHz": 4.0, "shape": "Sine", "phase": 0.0, "tempoSync": false}
	_edit("Add bus LFO", func(): _project().get_or_add("mixerModulators", []).append(lfo))
	return id
func remove_lfo(id: String) -> void:
	_edit("Remove bus LFO", func(): _project().get("mixerModulators", []).erase(Ops.find(_project().get("mixerModulators", []), id)))
func set_lfo_property(id: String, key: String, value) -> void:
	if Ops.find(_project().get("mixerModulators", []), id).is_empty(): return
	if key == "rateHz": value = clampf(float(value), 0.05, 20)
	if key == "amount": value = clampf(float(value), 0, 24)
	if Ops.find(_project().mixerModulators, id).get(key) == value: return
	_edit("LFO " + key, func(): Ops.find(_project().mixerModulators, id)[key] = value)

func _button(id: String, text: String, css: String, action: Callable) -> String:
	_clicks[id] = action
	return '<button id="%s" class="%s">%s</button>' % [id, css, Dom.escape(text)]
func _text_input(id: String, value, action: Callable, css: String = "num") -> String:
	_changes[id] = action
	return '<input id="%s" type="text" class="%s" value="%s">' % [id, css, Dom.escape(value)]
func _slider(id: String, minimum: float, maximum: float, step: float, reset: float, write: Callable, vertical: bool = false) -> String:
	_sliders[id] = {"min": minimum, "max": maximum, "step": step, "reset": reset, "write": write, "vertical": vertical, "value": reset}
	return '<div id="%s" class="%s"><div class="%s"></div>%s<div id="%s-thumb" class="%s"></div></div>' % [id, "fader" if vertical else "hslider", "rail" if vertical else "hfill", '<div class="zero" style="top:24px"></div>' if vertical else "", id, "thumb" if vertical else "hthumb"]
func _set_slider(id: String, value: float) -> void:
	if not _sliders.has(id): return
	_sliders[id].value = value
	var spec = _sliders[id]
	var fraction = clampf((value - spec.min) / (spec.max - spec.min), 0, 1)
	if spec.vertical: dom.style("#" + id + "-thumb", "top", "%.2fpx" % ((1 - fraction) * 136))
	else:
		dom.style("#" + id + " .hthumb", "left", "%.2fpx" % (fraction * 110))
		dom.style("#" + id + " .hfill", "width", "%.2fpx" % (fraction * 110 + 4))

func _refresh() -> void:
	var buses = _mixer().get("buses", [])
	var master = Ops.master(_mixer())
	var order = buses.map(func(bus): return bus.id)
	var reorder = order != _bus_order
	_bus_order = order
	for id in _rows.keys():
		if _bus(id).is_empty():
			host.remove_element("#bus-" + id)
			_rows.erase(id)
	for bus in buses:
		if not _rows.has(bus.id): _build_strip(bus, bus == master)
		var id = str(bus.id)
		var root = "#bus-" + id
		if reorder: host.move_element(root, "#master-slot" if bus == master else "#bus-strips", "")
		dom.text("#name-" + id, bus.get("name", ""))
		dom.toggle(root, "selected", selected_bus == id)
		dom.toggle(root, "snap-mode", not _snapshot().is_empty())
		for key in ["mute", "solo", "bypassEffects"]: dom.toggle("#" + key + "-" + id, "on", bus.get(key, false))
		var target = Ops.property("BusVolumeDb", id)
		var value = _shown_value(target, bus.get("volumeDb", 0))
		_set_slider("fader-" + id, value)
		dom.text("#db-" + id, "%.1f dB" % value)
		var scoped = not Ops.find_value(_snapshot(), target).is_empty()
		dom.hidden("#unscope-" + id, not scoped)
		dom.toggle("#fader-" + id, "scoped", scoped)
		dom.toggle("#fader-" + id, "unscoped-ref", not _snapshot().is_empty() and not scoped)
		dom.toggle("#db-" + id, "scoped", scoped)
		var lfo_count = 0
		for lfo in _project().get("mixerModulators", []):
			if lfo.get("$type") == "Lfo" and Ops.target_equal(lfo.get("target", {}), target): lfo_count += 1
		dom.text("#lfo-" + id, "LFO" if lfo_count == 0 else "LFO %d" % lfo_count)
		dom.toggle("#lfo-" + id, "on", lfo_count > 0)
		dom.value("#voices-" + id, bus.get("maxVoices", 0))
		if bus != master: dom.text("#route-" + id, "> " + str(_bus(bus.get("parent", "")).get("name", "(none)")))
		_refresh_chain(bus)
	_refresh_side()
	_refresh_popover()
	_refresh_lfo()
	automation.refresh()
	dom.hidden("#snap-banner", _snapshot().is_empty())
	dom.hidden("#snap-editor", _snapshot().is_empty())
	if not _snapshot().is_empty():
		_set_slider("snap-intensity", _snapshot_intensity)
		dom.text("#snap-intensity-text", "%d %%" % roundi(_snapshot_intensity * 100))
		dom.text("#snap-banner", "Editing snapshot: " + str(_snapshot().name))
		dom.text("#snap-editor-title", _snapshot().name)
		for key in ["attack", "hold", "decay", "sustain", "release"]: dom.value("#snap-" + key, _snapshot().get("intensity", {}).get(key, 1 if key == "sustain" else 0))

func _build_strip(bus: Dictionary, is_master: bool) -> void:
	var id = str(bus.id)
	var html = '<div id="bus-%s" class="strip%s"><div class="strip-head"><div id="name-%s" class="strip-name">%s</div>' % [id, " master" if is_master else "", id, Dom.escape(bus.name)]
	html += _text_input("rename-" + id, bus.name, func(v): rename_bus(id, v), "name-edit")
	if not is_master: html += _button("delete-" + id, "x", "strip-x", func(): delete_bus(id))
	html += '</div><div class="toggles">'
	for key in ["mute", "solo", "bypassEffects"]:
		html += _button(key + "-" + id, "M" if key == "mute" else "S" if key == "solo" else "B", "tog", func(): set_bus_property(id, key, not _bus(id).get(key, false)))
	html += '</div><div class="fader-row">' + _slider("fader-" + id, -80, 12, 0.1, 0, func(v): set_bus_property(id, "volumeDb", v), true)
	html += '<div class="meter">'
	for part in ["l", "r", "hotl", "hotr", "hold", "clip"]:
		var css = "bar l hot" if part == "hotl" else "bar r hot" if part == "hotr" else "bar " + part if part in ["l", "r"] else part
		html += '<div id="meter-%s-%s" class="%s" style="transform:%s"></div>' % [part, id, css, "none" if part == "clip" else "scaleY(0)"]
	html += '</div></div>' + _button("unscope-" + id, "x", "unscope-inline", func(): unscope(Ops.property("BusVolumeDb", id)))
	html += '<div id="db-%s" class="db"></div><div id="peak-%s" class="peak"></div>' % [id, id]
	if not is_master: html += _button("route-" + id, "Output", "route", func(): _route_popup(id))
	html += '<div class="fx-head"><div class="fx-label">EFFECTS</div>' + _button("fx-add-" + id, "+", "fx-add", func(): _effect_popup(id)) + '</div><div id="chain-%s" class="fx-list"></div><div class="fx-foot">' % id
	html += _button("lfo-" + id, "LFO", "fx-lfo", func(): _open_lfo(id))
	html += _button("automation-" + id, "Auto", "fx-lfo", func(): automation.open(id)) + '</div><div class="voices-row"><div class="field-label">Max voices</div>'
	html += _text_input("voices-" + id, bus.get("maxVoices", 0), func(v):
		if str(v).is_valid_float(): set_bus_property(id, "maxVoices", maxi(0, roundi(float(v))))) + '</div></div>'
	host.append_html("#master-slot" if is_master else "#bus-strips", html)
	host.set_attribute("#rename-" + id, "hidden", "")
	_clicks["meter-clip-" + id] = func():
		_rows[id].clip = false
		dom.toggle("#meter-clip-" + id, "on", false)
	_rows[id] = {"chain": "", "hold": -80.0, "age": 0.0, "clip": false}

func _refresh_chain(bus: Dictionary) -> void:
	var id = str(bus.id)
	var effects = bus.get("effects", [])
	var fader = clampi(bus.get("faderIndex", Ops.AFTER_ALL), 0, effects.size())
	var signature = str(fader)
	for effect in effects: signature += str(effect.id) + str(effect.typeName)
	if _rows[id].chain != signature:
		_rows[id].chain = signature
		var html = ""
		for slot in range(effects.size() + 1):
			if slot == fader:
				html += '<div id="slot-%s-%d" class="fx-item fx-fader"><div id="chain-drag-%s-%d" class="fx-name fx-fader-name">FADER</div></div>' % [id, slot, id, slot]
				continue
			var effect = effects[slot if slot < fader else slot - 1]
			var eid = str(effect.id)
			html += '<div id="slot-%s-%d" class="fx-item">' % [id, slot]
			html += _button("enable-" + eid, "", "fx-en", func(): _edit("Toggle effect", func(): _effect(id, eid).enabled = not _effect(id, eid).get("enabled", true)))
			html += '<div id="chain-drag-%s-%d" class="fx-name">%s</div>' % [id, slot, Dom.escape(Catalog.display_name(effect))]
			_clicks["chain-drag-" + id + "-" + str(slot)] = func(): _open_popover(id, eid)
			html += _button("remove-" + eid, "x", "fx-x", func(): remove_effect(id, eid)) + '</div>'
		host.set_inner_html("#chain-" + id, html)
		dom.forget()
	for slot in range(effects.size() + 1):
		if slot == fader: continue
		var index = slot if slot < fader else slot - 1
		var effect = effects[index]
		dom.toggle("#enable-" + str(effect.id), "on", effect.get("enabled", true))
		dom.toggle("#chain-drag-" + id + "-" + str(slot), "disabled", not effect.get("enabled", true))
		dom.text("#chain-drag-" + id + "-" + str(slot), Catalog.display_name(effect))
		dom.toggle("#slot-" + id + "-" + str(slot), "selected", selected_effect == effect.id)
		var scoped = false
		for value in _snapshot().get("values", []):
			var target = value.get("target", {})
			if target.get("kind") == "EffectParameter" and target.get("owner") == id and target.get("effectIndex", -1) == index: scoped = true
		dom.toggle("#slot-" + id + "-" + str(slot), "scoped", scoped)

func _refresh_side() -> void:
	for id in _vca_rows.keys():
		if Ops.find(_mixer().get("vcas", []), id).is_empty():
			host.remove_element("#vca-" + id)
			_vca_rows.erase(id)
	for vca in _mixer().get("vcas", []):
		var id = str(vca.id)
		if not _vca_rows.has(id):
			var html = '<div id="vca-%s" class="vca-row"><div class="row-head">' % id
			html += _button("vca-select-" + id, vca.name, "row-name", func(): _select("Vca", id))
			html += _text_input("vca-name-" + id, vca.name, func(v): set_vca_property(id, "name", str(v).strip_edges()), "name-edit")
			html += _button("vca-delete-" + id, "x", "strip-x", func(): delete_vca(id)) + '</div><div class="row-line">'
			html += _slider("vca-volume-" + id, -80, 12, 0.1, 0, func(v): set_vca_property(id, "volumeDb", v))
			html += '<div id="vca-db-%s" class="field-value"></div>' % id
			html += _button("vca-buses-" + id, "Buses", "pill", func(): _vca_popup(id)) + '</div></div>'
			host.append_html("#vca-list", html)
			host.set_attribute("#vca-name-" + id, "hidden", "")
			_vca_rows[id] = true
		dom.value("#vca-name-" + id, vca.name)
		dom.text("#vca-select-" + id, vca.name)
		_set_slider("vca-volume-" + id, vca.get("volumeDb", 0))
		dom.text("#vca-db-" + id, "%.1f dB" % vca.get("volumeDb", 0))
		var count = 0
		for bus in _mixer().get("buses", []):
			if bus.get("vcas", []).has(id): count += 1
		dom.text("#vca-buses-" + id, "Buses" if count == 0 else "%d bus%s" % [count, "" if count == 1 else "es"])
		dom.toggle("#vca-buses-" + id, "on", count > 0)
	for id in _snapshot_rows.keys():
		if Ops.find(_project().get("snapshots", []), id).is_empty():
			host.remove_element("#snapshot-" + id)
			_snapshot_rows.erase(id)
	for snapshot in _project().get("snapshots", []):
		var id = str(snapshot.id)
		if not _snapshot_rows.has(id):
			var html = '<div id="snapshot-%s" class="snap-row"><div class="row-head">' % id
			html += _button("snap-select-" + id, snapshot.name, "row-name", func(): select_snapshot(id))
			html += _text_input("snap-name-" + id, snapshot.name, func(v): set_snapshot_property(id, "name", str(v).strip_edges()), "name-edit")
			html += _button("snap-delete-" + id, "x", "strip-x", func(): delete_snapshot(id)) + '</div><div class="row-line"><div class="field-label">Priority</div>'
			html += _text_input("snap-priority-" + id, snapshot.get("priority", 0), func(v):
				if str(v).is_valid_float(): set_snapshot_property(id, "priority", roundi(float(v))))
			html += _button("snap-mode-" + id, snapshot.get("mode", "Blending"), "pill", func(): set_snapshot_property(id, "mode", "Overriding" if Ops.find(_project().snapshots, id).get("mode", "Blending") == "Blending" else "Blending"))
			html += '<div id="snap-count-%s" class="field-label"></div></div></div>' % id
			host.append_html("#snap-list", html)
			host.set_attribute("#snap-name-" + id, "hidden", "")
			_snapshot_rows[id] = true
		dom.text("#snap-select-" + id, snapshot.name)
		dom.value("#snap-name-" + id, snapshot.name)
		dom.value("#snap-priority-" + id, snapshot.get("priority", 0))
		dom.text("#snap-mode-" + id, snapshot.get("mode", "Blending"))
		dom.text("#snap-count-" + id, "%d values" % snapshot.get("values", []).size())
		dom.toggle("#snapshot-" + id, "selected", id == editing_snapshot)

func _popup(title: String, items: Array) -> void:
	_popup_actions = items
	_popup_open = true
	var html = '<div class="popup-title">%s</div>' % Dom.escape(title)
	for index in range(items.size()):
		html += '<button id="popup-item-%d" class="popup-item">%s</button>' % [index, Dom.escape(items[index].label)]
	host.set_inner_html("#popup", html)
	dom.hidden("#popup", false)
	var mouse = host.get_local_mouse_position()
	dom.style("#popup", "left", String.num(clampf(mouse.x, 0, maxf(0, host.size.x - 240)), 3) + "px")
	dom.style("#popup", "top", String.num(clampf(mouse.y, 0, maxf(0, host.size.y - 180)), 3) + "px")
func _route_popup(id: String) -> void:
	var items: Array = []
	for bus in Ops.routing_targets(_mixer(), id):
		var target = str(bus.id)
		items.append({"label": bus.name, "action": func(): set_bus_parent(id, target)})
	_popup("OUTPUT", items)
func _vca_popup(id: String) -> void:
	var items: Array = []
	for bus in _mixer().get("buses", []):
		var bid = str(bus.id)
		items.append({"label": ("[x] " if bus.get("vcas", []).has(id) else "[ ] ") + str(bus.name), "action": func(): toggle_vca_assignment(bid, id)})
	_popup("ASSIGN TO BUSES", items)
func _effect_popup(id: String) -> void:
	var items: Array = [{"label": "Ducking preset…", "action": func(): _duck_popup(id)}]
	for type in Catalog.available_types(): items.append({"label": Catalog.display_name(type), "action": func(): add_effect(id, type)})
	_popup("ADD EFFECT", items)
func _duck_popup(id: String) -> void:
	var items: Array = []
	for bus in _mixer().get("buses", []):
		if bus.id == id: continue
		var name = str(bus.name)
		items.append({"label": name, "action": func(): add_ducking(id, name)})
	_popup("DUCK FROM", items)

func _open_popover(bus_id: String, effect_id: String) -> void:
	selected_bus = bus_id
	selected_effect = effect_id
	_select("Effect", effect_id)
	_popover_bus = bus_id
	_popover_effect = effect_id
	_popover_structure = ""
	_close_lfo()
	_refresh_popover()
	_position_popover("#popover", 322)
func _close_popover() -> void:
	_popover_bus = ""
	_popover_effect = ""
	if dom != null: dom.hidden("#popover", true)
func _position_popover(selector: String, width: float) -> void:
	var mouse = host.get_local_mouse_position()
	dom.style(selector, "left", String.num(clampf(mouse.x + 8, 0, maxf(0, host.size.x - width)), 3) + "px")
	dom.style(selector, "top", String.num(clampf(mouse.y - 10, 0, maxf(0, host.size.y - 200)), 3) + "px")
	dom.hidden(selector, false)
func _refresh_popover() -> void:
	if _popover_bus.is_empty(): return
	var effect = _effect(_popover_bus, _popover_effect)
	if effect.is_empty():
		_close_popover()
		return
	var bid = _popover_bus
	var eid = _popover_effect
	var parameters = Catalog.parameters(effect.typeName)
	if _popover_structure != eid:
		_popover_structure = eid
		var html = ""
		for index in range(parameters.size()):
			var parameter = parameters[index]
			var key = str(parameter.name)
			var pid = "parameter-" + str(index)
			html += '<div class="param-row"><div id="%s-label" class="field-label">%s</div>' % [pid, Dom.escape(parameter.label)]
			match parameter.kind:
				"Float", "Int":
					html += _slider(pid, parameter.min, parameter.max, parameter.step, float(parameter.default), func(v): set_effect_parameter(bid, eid, key, v))
					html += _text_input(pid + "-input", "", func(v):
						if str(v).is_valid_float(): set_effect_parameter(bid, eid, key, clampf(float(v), parameter.min, parameter.max)))
				"String": html += _text_input(pid + "-input", "", func(v): set_effect_parameter(bid, eid, key, str(v)), "num text")
				"Bool": html += _button(pid + "-toggle", "Off", "pill", func(): set_effect_parameter(bid, eid, key, 0.0 if _parameter_value(bid, eid, parameter) >= 0.5 else 1.0))
				"Enum":
					html += _button(pid + "-enum", "", "pill", func():
						var items: Array = []
						for option in range(parameter.enumLabels.size()):
							var value = parameter.enumValues[option]
							items.append({"label": parameter.enumLabels[option], "action": func(): set_effect_parameter(bid, eid, key, value)})
						_popup(parameter.label, items))
			html += _button(pid + "-unscope", "x", "unscope-inline", func(): unscope(Ops.property("EffectParameter", bid, _bus(bid).effects.find(_effect(bid, eid)), key))) + '</div>'
		if parameters.is_empty(): html = '<div class="param-empty">%s</div>' % ("This effect has no editable parameters." if ClassDB.class_exists(effect.typeName) else "Class is not available in this engine build: " + Dom.escape(effect.typeName))
		host.set_inner_html("#popover-body", html)
		dom.forget()
	dom.text("#popover-title", str(_bus(bid).name) + " / " + Catalog.display_name(effect) + (" [snapshot " + str(_snapshot().name) + "]" if not _snapshot().is_empty() else ""))
	for index in range(parameters.size()):
		var parameter = parameters[index]
		var pid = "parameter-" + str(index)
		var target = Ops.property("EffectParameter", bid, _bus(bid).effects.find(effect), parameter.name)
		var scoped = not Ops.find_value(_snapshot(), target).is_empty()
		dom.hidden("#" + pid + "-unscope", not scoped)
		dom.toggle("#" + pid + "-label", "scoped", scoped)
		if parameter.kind == "String":
			dom.value("#" + pid + "-input", effect.get("stringParameters", {}).get(parameter.name, parameter.default))
			continue
		var value = _parameter_value(bid, eid, parameter)
		match parameter.kind:
			"Float", "Int":
				_set_slider(pid, value)
				dom.value("#" + pid + "-input", value)
			"Bool": dom.text("#" + pid + "-toggle", "On" if value >= 0.5 else "Off")
			"Enum":
				var option = parameter.enumValues.find(int(value))
				dom.text("#" + pid + "-enum", parameter.enumLabels[option] if option >= 0 else str(value))
func _parameter_value(bid: String, eid: String, parameter: Dictionary) -> float:
	var effect = _effect(bid, eid)
	var target = Ops.property("EffectParameter", bid, _bus(bid).get("effects", []).find(effect), parameter.name)
	return _shown_value(target, effect.get("parameters", {}).get(parameter.name, parameter.default))

func _open_lfo(id: String) -> void:
	_close_popover()
	_lfo_bus = id
	_lfo_structure = ""
	_refresh_lfo()
	_position_popover("#lfo-popover", 302)
func _close_lfo() -> void:
	_lfo_bus = ""
	if dom != null: dom.hidden("#lfo-popover", true)
func _refresh_lfo() -> void:
	if _lfo_bus.is_empty(): return
	if _bus(_lfo_bus).is_empty():
		_close_lfo()
		return
	var lfos: Array = []
	var signature = ""
	for lfo in _project().get("mixerModulators", []):
		if lfo.get("$type") == "Lfo" and Ops.target_equal(lfo.get("target", {}), Ops.property("BusVolumeDb", _lfo_bus)):
			lfos.append(lfo)
			signature += str(lfo.id)
	if _lfo_structure != signature or not host.has_element("#lfo-content"):
		_lfo_structure = signature
		var html = '<div id="lfo-content">'
		for lfo in lfos:
			var id = str(lfo.id)
			html += '<div class="lfo-row"><div class="lfo-row-head">'
			html += _button("lfo-shape-" + id, lfo.get("shape", "Sine"), "pill", func():
				var items: Array = []
				for shape in ["Sine", "Square", "Triangle", "SawUp", "SawDown", "Noise"]: items.append({"label": shape, "action": func(): set_lfo_property(id, "shape", shape)})
				_popup("WAVEFORM", items))
			html += _button("lfo-remove-" + id, "x", "fx-x", func(): remove_lfo(id)) + '</div><div class="param-row"><div class="field-label">Rate Hz</div>'
			html += _slider("lfo-rate-" + id, 0.05, 20, 0.05, 4, func(v): set_lfo_property(id, "rateHz", v))
			html += _text_input("lfo-rate-input-" + id, lfo.get("rateHz", 4), func(v):
				if str(v).is_valid_float(): set_lfo_property(id, "rateHz", float(v)))
			html += '</div><div class="param-row"><div class="field-label">Depth dB</div>'
			html += _slider("lfo-depth-" + id, 0, 24, 0.1, 3, func(v): set_lfo_property(id, "amount", v))
			html += _text_input("lfo-depth-input-" + id, lfo.get("amount", 3), func(v):
				if str(v).is_valid_float(): set_lfo_property(id, "amount", float(v))) + '</div></div>'
		if lfos.is_empty(): html += '<div class="param-empty">No LFO on this bus volume. + LFO adds a 4 Hz sine tremolo of 3 dB depth.</div>'
		host.set_inner_html("#lfo-popover-body", html + '</div>')
		dom.forget()
	dom.text("#lfo-popover-title", str(_bus(_lfo_bus).name) + " / Volume LFO")
	for lfo in lfos:
		var id = str(lfo.id)
		dom.text("#lfo-shape-" + id, lfo.get("shape", "Sine"))
		_set_slider("lfo-rate-" + id, lfo.get("rateHz", 4))
		_set_slider("lfo-depth-" + id, lfo.get("amount", 3))
		dom.value("#lfo-rate-input-" + id, lfo.get("rateHz", 4))
		dom.value("#lfo-depth-input-" + id, lfo.get("amount", 3))

func _meters(delta: float) -> void:
	_meter_clock += delta
	for bus in _mixer().get("buses", []):
		var id = str(bus.id)
		if not _rows.has(id): continue
		var index = AudioServer.get_bus_index(bus.get("name", ""))
		var left = AudioServer.get_bus_peak_volume_left_db(index, 0) if index >= 0 else -80.0
		var right = AudioServer.get_bus_peak_volume_right_db(index, 0) if index >= 0 else -80.0
		for channel in ["l", "r"]:
			var value = left if channel == "l" else right
			var pixels = roundf(clampf((value + 60) / 66, 0, 1) * 148)
			dom.style("#meter-" + channel + "-" + id, "transform", "scaleY(%s)" % String.num(minf(pixels, 121) / 148, 6))
			dom.style("#meter-hot" + channel + "-" + id, "transform", "scaleY(%s)" % String.num(maxf(0, pixels - 121) / 27, 6))
		var peak = maxf(left, right)
		var row = _rows[id]
		if peak >= row.hold:
			row.hold = peak
			row.age = 0.0
		else:
			row.age += delta
			if row.age > 1.5: row.hold = maxf(peak, row.hold - 24 * delta)
		dom.style("#meter-hold-" + id, "transform", "translateY(-%dpx)" % roundi(clampf((row.hold + 60) / 66, 0, 1) * 148))
		if peak > 0 and not row.clip:
			row.clip = true
			dom.toggle("#meter-clip-" + id, "on", true)
		if _meter_clock > 0.25 and bus == Ops.master(_mixer()): dom.text("#peak-" + id, "peak %.1f dB" % (roundf(row.hold * 2) / 2))
	if _meter_clock > 0.25: _meter_clock = 0.0

func _on_dom_event(type: String, id: String, details: Dictionary) -> void:
	if type == "click":
		if _suppress_click:
			_suppress_click = false
			return
		if id.begins_with("popup-item-"):
			var index = int(id.trim_prefix("popup-item-"))
			dom.hidden("#popup", true)
			_popup_open = false
			if index < _popup_actions.size(): _popup_actions[index].action.call()
		elif _clicks.has(id): _clicks[id].call()
	elif type == "change" or type == "blur" or (type == "keydown" and details.get("key") == "Enter"):
		if _changes.has(id):
			_changes[id].call(host.get_value("#" + id))
			if id.begins_with("rename-"):
				host.set_attribute("#" + id, "hidden", "")
				host.remove_attribute("#name-" + id.trim_prefix("rename-"), "hidden")
			elif id.begins_with("vca-name-") or id.begins_with("snap-name-"):
				host.set_attribute("#" + id, "hidden", "")
				host.remove_attribute("#" + id.replace("-name-", "-select-"), "hidden")
	elif type == "keydown" and details.get("key") == "Escape":
		if id.begins_with("rename-"):
			host.set_attribute("#" + id, "hidden", "")
			host.remove_attribute("#name-" + id.trim_prefix("rename-"), "hidden")
		elif id.begins_with("vca-name-") or id.begins_with("snap-name-"):
			host.set_attribute("#" + id, "hidden", "")
			host.remove_attribute("#" + id.replace("-name-", "-select-"), "hidden")
		else: _escape()
	elif type == "pointerdown": _pointer_down(id, details)
	elif type == "pointermove": _pointer_move(details)
	elif type in ["pointerup", "pointercancel"]: _pointer_up(details, type == "pointercancel")
	elif type == "wheel":
		var slider = id.trim_suffix("-thumb")
		if _sliders.has(slider):
			var spec = _sliders[slider]
			var delta = float(details.get("delta_y", 0))
			if delta == 0: delta = float(details.get("delta_x", 0))
			if delta == 0: return
			var wheel_step = 0.5 if slider.begins_with("fader-") or slider.begins_with("vca-volume-") else spec.step * 2 if slider.begins_with("lfo-") else 0.05 if slider == "snap-intensity" else spec.step
			wheel_step *= 0.1 if details.get("shift_key", false) else 1.0
			spec.write.call(clampf(spec.value + (-wheel_step if delta > 0 else wheel_step), spec.min, spec.max))

func _pointer_down(id: String, details: Dictionary) -> void:
	if details.get("button", 0) != 0: return
	var slider = id.trim_suffix("-thumb")
	var position: Vector2 = details.get("position", Vector2.ZERO)
	if not id.begins_with("popup-item-") and not host.get_element_rect("#popup").has_point(position):
		dom.hidden("#popup", true)
		_popup_open = false
	if not _popover_bus.is_empty() and not id.begins_with("chain-drag-") and not host.get_element_rect("#popover").has_point(position) and not id.begins_with("popup-item-"):
		_close_popover()
	if not _lfo_bus.is_empty() and not id.begins_with("lfo-") and not host.get_element_rect("#lfo-popover").has_point(position) and not id.begins_with("popup-item-"):
		_close_lfo()
	var now = Time.get_ticks_msec() / 1000.0
	var double_press = id == _last_press_id and now - _last_press_time < 0.35
	_last_press_id = id
	_last_press_time = -INF if double_press else now
	if double_press and (id.begins_with("vca-select-") or id.begins_with("snap-select-")):
		var input_id = id.replace("-select-", "-name-")
		host.remove_attribute("#" + input_id, "hidden")
		host.set_attribute("#" + id, "hidden", "")
		host.focus_element.call_deferred("#" + input_id)
		_suppress_click = true
		return
	if double_press and id.begins_with("name-"):
		var bid = id.trim_prefix("name-")
		if _bus(bid) != Ops.master(_mixer()):
			host.remove_attribute("#rename-" + bid, "hidden")
			host.set_attribute("#" + id, "hidden", "")
			host.focus_element.call_deferred("#rename-" + bid)
		return
	if _sliders.has(slider):
		if slider.begins_with("fader-"):
			selected_bus = slider.trim_prefix("fader-")
			_select("Bus", selected_bus)
		if double_press:
			_sliders[slider].write.call(_sliders[slider].reset)
			return
		var spec = _sliders[slider]
		_drag = {"kind": "slider", "id": slider, "pointer": details.get("pointer_id", 0), "origin": position, "value": spec.value}
		host.set_pointer_capture("#" + slider, _drag.pointer)
		var bounds: Rect2 = host.get_element_rect("#" + slider)
		var thumb = 12.0 if spec.vertical else 8.0
		var travel = 136.0 if spec.vertical else 110.0
		var inner = position.y - bounds.position.y if spec.vertical else position.x - bounds.position.x
		var fraction = clampf((inner - thumb / 2) / travel, 0, 1)
		if spec.vertical: fraction = 1 - fraction
		var jumped = lerpf(spec.min, spec.max, fraction)
		if absf(jumped - spec.value) > (spec.max - spec.min) * thumb / (travel + thumb):
			_drag.value = clampf(jumped, spec.min, spec.max)
			spec.value = spec.min + roundf((_drag.value - spec.min) / spec.step) * spec.step
			spec.write.call(spec.value)
	elif id.begins_with("chain-drag-"):
		var parts = id.trim_prefix("chain-drag-").split("-")
		selected_bus = parts[0]
		_drag = {"kind": "chain", "id": id, "bus": parts[0], "slot": int(parts[1]), "pointer": details.get("pointer_id", 0), "origin": position, "active": false, "to": -1}
		host.set_pointer_capture("#" + id, _drag.pointer)
	elif id.begins_with("name-"):
		var bid = id.trim_prefix("name-")
		selected_bus = bid
		_select("Bus", bid)
		if _bus(bid) == Ops.master(_mixer()): return
		_drag = {"kind": "bus", "id": id, "bus": bid, "pointer": details.get("pointer_id", 0), "origin": position, "active": false, "to": -1}
		host.set_pointer_capture("#" + id, _drag.pointer)

func _pointer_move(details: Dictionary) -> void:
	if _drag.is_empty(): return
	var position: Vector2 = details.get("position", Vector2.ZERO)
	if _drag.kind == "slider":
		var spec = _sliders[_drag.id]
		var delta = position.y - _drag.origin.y if spec.vertical else position.x - _drag.origin.x
		if spec.vertical: delta = -delta
		var travel = 136.0 if spec.vertical else 110.0
		var fine = 0.1 if details.get("shift_key", false) else 1.0
		_drag.value = clampf(_drag.value + delta / travel * (spec.max - spec.min) * fine, spec.min, spec.max)
		_drag.origin = position
		var value = spec.min + roundf((_drag.value - spec.min) / spec.step) * spec.step
		if not is_equal_approx(value, spec.value):
			spec.value = value
			spec.write.call(value)
		return
	if position.distance_to(_drag.origin) < 5 and not _drag.active: return
	_drag.active = true
	_drag.to = -1
	if _drag.kind == "chain":
		for slot in range(_bus(_drag.bus).get("effects", []).size() + 1):
			var selector = "#slot-%s-%d" % [_drag.bus, slot]
			dom.toggle(selector, "drop-before", false)
			dom.toggle(selector, "drop-after", false)
			var bounds: Rect2 = host.get_element_rect(selector)
			if bounds.has_point(position) and slot != _drag.slot:
				var after = position.y > bounds.get_center().y
				var target = slot + (1 if after else 0)
				if target > _drag.slot: target -= 1
				_drag.to = target
				dom.toggle(selector, "drop-after" if after else "drop-before", true)
	else:
		var buses = _mixer().get("buses", [])
		for index in range(buses.size()):
			var bus = buses[index]
			var selector = "#bus-" + str(bus.id)
			dom.toggle(selector, "drop-before", false)
			dom.toggle(selector, "drop-after", false)
			if bus.id == _drag.bus or bus == Ops.master(_mixer()): continue
			var bounds: Rect2 = host.get_element_rect(selector)
			if bounds.has_point(position):
				var after = position.x > bounds.get_center().x
				var target = index + (1 if after else 0)
				if target > buses.find(_bus(_drag.bus)): target -= 1
				_drag.to = target
				dom.toggle(selector, "drop-after" if after else "drop-before", true)

func _pointer_up(_details: Dictionary, cancelled: bool) -> void:
	if _drag.is_empty(): return
	var drag = _drag
	_drag = {}
	if host.has_pointer_capture("#" + str(drag.id), drag.pointer): host.release_pointer_capture("#" + str(drag.id), drag.pointer)
	if drag.kind == "slider":
		document.end_continuous()
		return
	_suppress_click = drag.active
	if not cancelled and drag.active and drag.to >= 0:
		if drag.kind == "chain": move_chain_row(drag.bus, drag.slot, drag.to)
		else: move_bus(drag.bus, drag.to)
	for bus in _mixer().get("buses", []):
		dom.toggle("#bus-" + str(bus.id), "drop-before", false)
		dom.toggle("#bus-" + str(bus.id), "drop-after", false)
		for slot in range(bus.get("effects", []).size() + 1):
			var selector = "#slot-%s-%d" % [bus.id, slot]
			if host.has_element(selector):
				dom.toggle(selector, "drop-before", false)
				dom.toggle(selector, "drop-after", false)
func _escape() -> void:
	if dom == null: return
	if _popup_open:
		_popup_open = false
		dom.hidden("#popup", true)
		return
	if not _popover_bus.is_empty():
		_close_popover()
		return
	if not _lfo_bus.is_empty():
		_close_lfo()
		return
	if not automation.bus_id.is_empty():
		automation.close()
		return
	if not editing_snapshot.is_empty(): select_snapshot("")
func handle_command(command: String) -> bool:
	if command == "Escape":
		_escape()
		return true
	if command != "Delete" or (host != null and not host.get_active_element_id().is_empty() and _changes.has(host.get_active_element_id())): return false
	match _selected_kind:
		"Bus": delete_bus(_selected_id)
		"Effect": remove_effect(selected_bus, _selected_id)
		"Vca": delete_vca(_selected_id)
		"Snapshot": delete_snapshot(_selected_id)
		_: return false
	return true

func select_subject(kind: int, id: String, owner: String = "", index: int = -1) -> void:
	match kind:
		5:
			selected_bus = id
			selected_effect = ""
			_select("Bus", id)
		6:
			selected_bus = owner
			selected_effect = id
			_select("Effect", id)
			if host != null and _bound and index >= 0: _open_popover(owner, id)
		7: _select("Vca", id)
		8: select_snapshot(id)
