@tool
extends RefCounted

const SnapshotInstance = preload("snapshot_instance.gd")
const CurveMath = preload("../Evaluation/curve_math.gd")
const PropertyBlend = preload("../Evaluation/property_blend.gd")
const Modulators = preload("../Evaluation/modulators.gd")

var _system_ref: WeakRef
var system:
	get:
		return _system_ref.get_ref() if _system_ref != null else null
	set(value):
		_system_ref = weakref(value) if value != null else null
var random
var active_snapshots: Array = []
var active_snapshot_count: int:
	get:
		return active_snapshots.size()
var bus_count: int:
	get:
		return _buses.size()
var _buses: Array = []
var _vcas: Array = []
var _globals
var _commands: Array
var _bus_by_id: Dictionary = {}
var _bus_by_name: Dictionary = {}
var _vca_by_id: Dictionary = {}
var _vca_by_name: Dictionary = {}
var _properties: Dictionary = {}
var _authored: Array[float] = []
var _values: Array[float] = []
var _effects: Array = []
var _effect_slots: Array = []
var _pitch_effect: Array[int] = []
var _pitch_merged: Array[bool] = []
var _vca_properties: Array = []
var _bus_offsets: Array[float] = []
var _vca_offsets: Array[float] = []
var _mute: Array[bool] = []
var _solo: Array[bool] = []
var _last_bus: Array = []
var _last_enabled: Array = []
var _last_effect: Array = []
var _snapshots: Dictionary = {}
var _pool: Array = []
var _automation: Array = []
var _modulators: Array = []
var _effect_base: int
var _force_emit: bool = true

func _init(project: Dictionary, globals, commands: Array, rng, platform: String = "") -> void:
	_globals = globals
	_commands = commands
	random = rng
	_buses = project.get("mixer", {}).get("buses", [])
	_vcas = project.get("mixer", {}).get("vcas", [])
	_effect_base = bus_count * 2 + _vcas.size()
	_authored.resize(_effect_base)
	_authored.fill(0.0)
	for i in bus_count:
		var bus: Dictionary = _buses[i]
		_bus_by_id[bus.id] = i
		_bus_by_name[bus.name] = i
		_authored[i] = bus.get("volumeDb", 0.0)
		_authored[bus_count + i] = 1.0
		_properties[_key({"kind": "BusVolumeDb", "owner": bus.id})] = i
		_properties[_key({"kind": "BusPitch", "owner": bus.id})] = bus_count + i
		_pitch_effect.append(-1)
		_pitch_merged.append(false)
		_bus_offsets.append(0.0)
		_mute.append(false)
		_solo.append(false)
		_last_bus.append({})
		var effects: Array = bus.get("effects", [])
		for e in effects.size():
			var effect: Dictionary = effects[e]
			if _pitch_effect[i] < 0 and effect.get("typeName", "") == "AudioEffectPitchShift":
				_pitch_effect[i] = e
			_effects.append({"bus": i, "index": e, "effect": effect})
			_last_enabled.append(null)
			for parameter in effect.get("parameters", {}):
				var is_pitch: bool = e == _pitch_effect[i] and parameter == "pitch_scale"
				if is_pitch:
					_pitch_merged[i] = true
				_properties[_key({"kind": "EffectParameter", "owner": bus.id, "effectIndex": e, "name": parameter})] = _authored.size()
				_authored.append(float(effect.parameters[parameter]))
				_effect_slots.append({"bus": i, "index": e, "parameter": parameter, "pitchBus": i if is_pitch else -1})
				_last_effect.append(null)
	for v in _vcas.size():
		var vca: Dictionary = _vcas[v]
		_vca_by_id[vca.id] = v
		_vca_by_name[vca.name] = v
		_authored[2 * bus_count + v] = vca.get("volumeDb", 0.0)
		_properties[_key({"kind": "VcaVolumeDb", "owner": vca.id})] = 2 * bus_count + v
		_vca_offsets.append(0.0)
	for bus in _buses:
		var slots: Array[int] = []
		for id in bus.get("vcas", []):
			if _vca_by_id.has(id):
				slots.append(2 * bus_count + _vca_by_id[id])
		_vca_properties.append(slots)
	if not platform.is_empty():
		for override in project.get("platformOverrides", []):
			if str(override.get("platform", "")).to_lower() == platform.to_lower():
				for value in override.get("values", []):
					var slot: int = _property(value.get("target", {}))
					if slot >= 0:
						_authored[slot] = value.get("value", 0.0)
	_values.assign(_authored)
	for snapshot in project.get("snapshots", []):
		var values: Array = []
		for value in snapshot.get("values", []):
			values.append({"property": _property(value.get("target", {})), "value": value.get("value", 0.0)})
		_snapshots[snapshot.get("name", "")] = {"snapshot": snapshot, "values": values}
	for lane in project.get("mixerAutomation", []):
		_register_automation(lane)
	for modulator in project.get("mixerModulators", []):
		_register_modulator(modulator)

static func _key(target: Dictionary) -> Array:
	return [target.get("kind", "BusVolumeDb"), target.get("owner", ""), int(target.get("effectIndex", -1)), target.get("name")]

func _property(target: Dictionary) -> int:
	return _properties.get(_key(target), -1)

func record_call(target, method: String, name = null, text = null, value: float = 0.0, flag: bool = false, mode: int = 0) -> void:
	if system != null and system.recorder != null:
		system.recorder.record_call(target, method, name, text, value, flag, mode)

func _bus_index(id_or_name: String) -> int:
	var index: int = _bus_by_id.get(id_or_name, _bus_by_name.get(id_or_name, -1))
	assert(index >= 0, "Unknown bus: " + id_or_name)
	return index

func _vca_index(id_or_name: String) -> int:
	var index: int = _vca_by_id.get(id_or_name, _vca_by_name.get(id_or_name, -1))
	assert(index >= 0, "Unknown VCA: " + id_or_name)
	return index

func bus_name(id: String) -> String:
	return _buses[_bus_index(id)].name

func try_get_bus(name: String) -> String:
	return _buses[_bus_by_name[name]].id if _bus_by_name.has(name) else ""

func try_get_vca(name: String) -> String:
	return _vcas[_vca_by_name[name]].id if _vca_by_name.has(name) else ""

func effective_bus_volume_db(id: String) -> float:
	return _values[_bus_index(id)]

func effective_bus_pitch(id: String) -> float:
	return _values[bus_count + _bus_index(id)]

func effective_vca_volume_db(id: String) -> float:
	return _values[2 * bus_count + _vca_index(id)]

func set_bus_volume(bus: String, db: float) -> void:
	record_call(self, "SetBusVolume", bus, null, db)
	_bus_offsets[_bus_index(bus)] = db

func set_bus_mute(bus: String, mute: bool) -> void:
	record_call(self, "SetBusMute", bus, null, 0.0, mute)
	_mute[_bus_index(bus)] = mute

func set_bus_solo(bus: String, solo: bool) -> void:
	record_call(self, "SetBusSolo", bus, null, 0.0, solo)
	_solo[_bus_index(bus)] = solo

func set_vca_volume(vca: String, db: float) -> void:
	record_call(self, "SetVcaVolume", vca, null, db)
	_vca_offsets[_vca_index(vca)] = db

func has_snapshot(name: String) -> bool:
	return _snapshots.has(name)

func start_snapshot(name: String):
	assert(has_snapshot(name), "Unknown snapshot: " + name)
	if not has_snapshot(name):
		return null
	var instance = _pool.pop_back() if not _pool.is_empty() else SnapshotInstance.new()
	instance.activate(_snapshots[name], self)
	var index: int = active_snapshots.size()
	while index > 0 and active_snapshots[index - 1].snapshot.get("priority", 0) > instance.snapshot.get("priority", 0):
		index -= 1
	active_snapshots.insert(index, instance)
	if system != null and system.recorder != null:
		system.recorder.created(instance, "StartSnapshot", name)
	return instance

func _register_automation(lane: Dictionary) -> bool:
	var slot: int = _property(lane.get("target", {}))
	var parameter = _globals.get_state(lane.get("parameter", "")) if _globals != null else null
	if slot < 0 or parameter == null:
		return false
	_automation.append({"id": lane.get("id", ""), "property": slot, "parameter": parameter, "curve": lane.get("curve", {})})
	return true

func add_automation(lane: Dictionary) -> bool:
	var valid: bool = _register_automation(lane)
	assert(valid, "Automation needs a mixer property and global parameter")
	return valid

func remove_automation(id: String) -> bool:
	for i in _automation.size():
		if _automation[i].id == id:
			_automation.remove_at(i)
			return true
	return false

func _register_modulator(modulator: Dictionary) -> bool:
	var kind: String = modulator.get("$type", "")
	var target: Dictionary = modulator.get("target", {})
	var slot: int = _property(target)
	if slot < 0 or kind not in ["Lfo", "Random"]:
		return false
	var lfo = Modulators.LfoState.new(modulator, random) if kind == "Lfo" else null
	var offset: float = random.randf() * 2.0 - 1.0 if lfo == null else 0.0
	_modulators.append({"modulator": modulator, "property": slot, "multiply": target.get("kind") == "BusPitch", "lfo": lfo, "random": offset})
	return true

func add_modulator(modulator: Dictionary) -> bool:
	var valid: bool = _register_modulator(modulator)
	assert(valid, "Mixer modulators require Lfo/Random and a mixer property")
	return valid

func remove_modulator(id: String) -> bool:
	for i in _modulators.size():
		if _modulators[i].modulator.get("id", "") == id:
			_modulators.remove_at(i)
			return true
	return false

func update(dt: float) -> void:
	for i in range(active_snapshots.size() - 1, -1, -1):
		var instance = active_snapshots[i]
		instance.envelope.advance(dt)
		if instance.envelope.is_finished:
			active_snapshots.remove_at(i)
			instance.deactivate()
			_pool.append(instance)
	for i in _authored.size():
		_values[i] = _authored[i]
	for instance in active_snapshots:
		var intensity: float = instance.effective_intensity
		if intensity <= 0.0:
			continue
		for value in instance.resolved_values:
			var slot: int = value.property
			if slot >= 0:
				_values[slot] = PropertyBlend.apply(_values[slot], _authored[slot], value.value, intensity, instance.snapshot.get("mode", "Blending"))
	for lane in _automation:
		_values[lane.property] += CurveMath.evaluate(lane.curve, lane.parameter.value)
	for slot in _modulators:
		var wave: float = slot.random
		if slot.lfo != null:
			wave = slot.lfo.advance(dt, slot.modulator.get("rateHz", 1.0)) if dt > 0.0 else slot.lfo.current
		var contribution: float = slot.modulator.get("amount", 0.0) * wave
		if slot.multiply:
			_values[slot.property] *= maxf(0.01, 1.0 + contribution)
		else:
			_values[slot.property] += contribution
	for v in _vcas.size():
		_values[2 * bus_count + v] += _vca_offsets[v]
	for i in bus_count:
		_values[i] += _bus_offsets[i]
		for v in _vca_properties[i]:
			_values[i] += _values[v]
	for i in bus_count:
		var bus: Dictionary = _buses[i]
		_emit_bus(i, "volumeDb", _values[i], "BusSetVolume")
		_emit_bus(i, "mute", bus.get("mute", false) or _mute[i], "BusSetMute")
		_emit_bus(i, "solo", bus.get("solo", false) or _solo[i], "BusSetSolo")
		_emit_bus(i, "bypass", bus.get("bypassEffects", false), "BusSetBypass")
		if _pitch_effect[i] >= 0 and not _pitch_merged[i]:
			var pitch: float = _values[bus_count + i]
			if _force_emit or _last_bus[i].get("pitch") != pitch:
				_last_bus[i].pitch = pitch
				_commands.append({"type": "EffectSetParameter", "bus": bus.name, "effectIndex": _pitch_effect[i], "parameter": "pitch_scale", "value": pitch})
	for e in _effects.size():
		var effect: Dictionary = _effects[e]
		var enabled: bool = effect.effect.get("enabled", true)
		if _force_emit or _last_enabled[e] != enabled:
			_last_enabled[e] = enabled
			_commands.append({"type": "EffectSetEnabled", "bus": _buses[effect.bus].name, "effectIndex": effect.index, "enabled": enabled})
	for s in _effect_slots.size():
		var slot: Dictionary = _effect_slots[s]
		var value: float = _values[_effect_base + s]
		if slot.pitchBus >= 0:
			value *= _values[bus_count + slot.pitchBus]
		if _force_emit or _last_effect[s] != value:
			_last_effect[s] = value
			_commands.append({"type": "EffectSetParameter", "bus": _buses[slot.bus].name, "effectIndex": slot.index, "parameter": slot.parameter, "value": value})
	_force_emit = false

func _emit_bus(index: int, field: String, value, type: String) -> void:
	if _force_emit or _last_bus[index].get(field) != value:
		_last_bus[index][field] = value
		_commands.append({"type": type, "bus": _buses[index].name, field: value})
