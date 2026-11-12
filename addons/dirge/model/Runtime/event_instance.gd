@tool
extends RefCounted

signal callback(instance, event: Dictionary)

const Bank = preload("parameter_bank.gd")
const Timeline = preload("timeline.gd")
const Instrument = preload("instrument_playback.gd")
const Spatial = preload("spatial.gd")
const ModEval = preload("mod_eval.gd")

var _system_ref: WeakRef
var _description_ref: WeakRef
var _parent_ref: WeakRef
var system:
	get: return _system_ref.get_ref() if _system_ref != null else null
	set(value): _system_ref = weakref(value) if value != null else null
var description:
	get: return _description_ref.get_ref() if _description_ref != null else null
	set(value): _description_ref = weakref(value) if value != null else null
var local_parameters
var state: String = "Stopped"
var is_virtual: bool = false
var callback_mask: int = 1023
var attributes: Dictionary = Spatial.origin()
var paused: bool = false
var is_destroyed: bool = false
var start_time: float = 0.0
var steal_virtual: bool = false
var parent:
	get: return _parent_ref.get_ref() if _parent_ref != null else null
	set(value): _parent_ref = weakref(value) if value != null else null
var depth: int = 0
var inherited_volume_db: float = 0.0
var inherited_pitch: float = 1.0
var event_volume_db: float = 0.0
var event_pitch: float = 1.0
var can_trigger: bool:
	get: return state == "Playing" or state == "Sustaining"
var timeline_position: float:
	get: return _seek_to if _seek_pending else _timeline.position
	set(value):
		_record("Seek", null, null, value)
		_seek_to = maxf(0, value)
		if state == "Stopped" or state == "Starting":
			_timeline.reset(_seek_to)
		else:
			_seek_pending = true
		_capture_update_boundary()
var is_in_transition: bool:
	get: return _timeline.transition != null
var transition_position: float:
	get: return _timeline.transition_elapsed if is_in_transition else 0.0
var _timeline
var _instruments: Array = []
var _mods: Array = []
var _released: bool = false
var _seek_pending: bool = false
var _seek_to: float = 0.0
var _update_offset = null
var _clocked_start: bool = false
var _defer_native_seek: bool = false
var _volume_db: float = 0.0
var _pitch: float = 1.0
var _programmer_asset: String = ""
var _start_delay: float = 0.0

func _init(root, desc):
	system = root
	description = desc
	local_parameters = Bank.new(desc.parameters)
	for info in desc.instruments:
		_instruments.append(Instrument.new(info, self, system))
	_timeline = Timeline.new(self, desc)

func reinitialize() -> void:
	for parameter in local_parameters.all:
		parameter.set_value(parameter.definition.get("default", 0), true)
		parameter.rebase()
	for instrument in _instruments:
		instrument.reset()
	_timeline.reset(0)
	state = "Stopped"
	paused = false
	_released = false
	is_destroyed = false
	is_virtual = false
	_seek_pending = false
	_seek_to = 0
	_update_offset = null
	_clocked_start = false
	_defer_native_seek = false
	_volume_db = 0
	_pitch = 1
	_programmer_asset = ""
	attributes = Spatial.origin()
	start_time = 0
	_start_delay = 0
	steal_virtual = false
	parent = null
	depth = 0
	inherited_volume_db = 0
	inherited_pitch = 1
	event_volume_db = description.event.get("macros", {}).get("volumeDb", 0)
	event_pitch = description.event.get("macros", {}).get("pitch", 1)
	callback_mask = 1023
	_mods.clear()
	for connection in callback.get_connections():
		callback.disconnect(connection.callable)

func _record(method: String, name = null, text = null, value: float = 0, flag: bool = false, mode: int = 0) -> void:
	if system.recorder != null:
		system.recorder.record_call(self, method, name, text, value, flag, mode)

func start() -> void:
	_record("Start")
	if is_destroyed or state != "Stopped":
		return
	var macros = description.event.get("macros", {})
	if macros.get("cooldown", 0) > 0 and system.time - description.last_start_time < macros.cooldown:
		return
	if macros.get("maxInstances", 0) > 0 and description.started.size() >= macros.maxInstances and not _steal(macros.get("stealing", "Oldest")):
		return
	description.last_start_time = system.time
	start_time = system.time
	_update_offset = null
	_clocked_start = false
	state = "Starting"
	if not _capture_update_boundary():
		_start_delay = _quantized_start_delay()
	_mods = ModEval.initialize(description.modulators, self, system.random)
	if not description.started.has(self):
		description.started.append(self)

func _capture_update_boundary() -> bool:
	var elapsed = system.mutation_update_offset()
	if elapsed == null:
		return false
	set_update_offset(elapsed)
	return true

func set_update_offset(elapsed: float) -> void:
	_update_offset = elapsed
	if state == "Starting":
		_clocked_start = true
		_start_delay = _quantized_start_delay(elapsed)
	_record("SetUpdateOffset", null, null, elapsed)

func _quantized_start_delay(elapsed: float = 0) -> float:
	var macros = description.event.get("macros", {})
	var quantization = macros.get("startQuantization", "None")
	var conductor = system.conductor
	if quantization == "None" or conductor == null or conductor == self or not conductor.can_trigger:
		return 0.0
	return conductor.delay_to_next_grid(quantization, macros.get("scheduleLookahead", 0), elapsed)

func delay_to_next_grid(quantization: String, lookahead: float, elapsed: float = 0) -> float:
	var position = _timeline.position + elapsed
	var at = description.next_grid(position, quantization, false)
	if at == null:
		return 0.0
	var delay = maxf(0, at - position)
	for _guard in range(64):
		if delay >= lookahead:
			break
		var after = description.next_grid(at, quantization, false)
		if after == null or after <= at:
			break
		at = after
		delay = at - position
	return delay

func _steal(mode: String) -> bool:
	if mode == "None":
		return false
	if mode == "Virtualize":
		steal_virtual = true
		return true
	var victim = null
	var best = 0.0
	for candidate in description.started:
		if candidate.state == "Stopping":
			victim = candidate
			break
		var score = system.time - candidate.start_time
		if mode == "Quietest": score = -candidate.event_volume_db
		elif mode == "Furthest": score = candidate.distance_to_nearest_listener()
		if victim == null or score > best:
			victim = candidate
			best = score
	if victim == null:
		return false
	victim.raise_callback(256, {"type": "StolenCallback"})
	system.enter_internal()
	victim.stop("Immediate")
	system.exit_internal()
	return true

func stop(mode: String = "AllowFadeout") -> void:
	_record("Stop", null, null, 0, false, 1 if mode == "Immediate" else 0)
	if is_destroyed or state == "Stopped":
		return
	if state == "Starting":
		_finish()
		return
	if mode == "Immediate":
		for instrument in _instruments:
			instrument.stop(mode)
		_finish()
		return
	if state == "Stopping":
		return
	state = "Stopping"
	_timeline.key_off()
	ModEval.release(description.modulators, _mods)
	for instrument in _instruments:
		instrument.stop(mode, description.max_release)
	if not _any_busy():
		_finish()

func key_off() -> void:
	_record("KeyOff")
	if is_destroyed:
		return
	var held = _timeline.holding
	_timeline.key_off()
	if held and state == "Sustaining":
		state = "Playing"
	ModEval.release(description.modulators, _mods)
	for instrument in _instruments:
		instrument.key_off()

func set_paused(value: bool) -> void:
	_record("SetPaused", null, null, 0, value)
	if paused == value:
		return
	paused = value
	for instrument in _instruments:
		for child in instrument.children:
			child.set_paused(value)

func resolve_parameter(id_or_name: String):
	var parameter = local_parameters.get_state(id_or_name)
	return parameter if parameter != null else system.global_parameters.get_state(id_or_name)

func set_parameter(name: String, value: float, immediate: bool = false) -> void:
	_record("SetParameter", name, null, value, immediate)
	var parameter = resolve_parameter(name)
	if parameter != null:
		parameter.set_value(value, immediate)

func get_parameter(name: String) -> float:
	var parameter = resolve_parameter(name)
	return parameter.value if parameter != null else 0.0

func get_parameter_velocity(name: String) -> float:
	var parameter = resolve_parameter(name)
	return parameter.velocity if parameter != null else 0.0

func set_parameter_by_label(name: String, label: String) -> void:
	_record("SetParameterByLabel", name, label)
	var parameter = resolve_parameter(name)
	if parameter != null:
		var index = parameter.definition.get("labels", []).find(label)
		if index >= 0:
			parameter.set_value(parameter.definition.get("minimum", 0) + index)

func set_3d_attributes(value: Dictionary) -> void:
	attributes = Spatial.attributes(value)
	if system.recorder != null:
		system.recorder.call_3d(self, "Set3DAttributes", 0, attributes, 0)

func set_volume(db: float) -> void:
	_record("SetVolume", null, null, db)
	_volume_db = db

func set_pitch(ratio: float) -> void:
	_record("SetPitch", null, null, ratio)
	_pitch = maxf(0.01, ratio)

func set_programmer_sound(asset) -> void:
	_programmer_asset = system.asset_path(asset)
	_record("SetProgrammerSound", _programmer_asset)

func release() -> void:
	_record("Release")
	if is_destroyed:
		return
	_released = true
	if state == "Stopped" and parent == null:
		system.request_destroy(self)

func _finish() -> void:
	state = "Stopped"
	_timeline.key_off()
	_timeline.reset(_timeline.position)
	_seek_pending = false
	description.started.erase(self)
	if description.event.get("isConductor", false):
		system.on_conductor_stopped(self)
	is_virtual = false
	steal_virtual = false
	raise_callback(2, {"type": "StoppedCallback"})
	if _released and parent == null:
		system.request_destroy(self)

func destroy() -> void:
	if is_destroyed:
		return
	if state != "Stopped":
		stop("Immediate")
	is_destroyed = true
	for connection in callback.get_connections():
		callback.disconnect(connection.callable)
	description.recycle(self)

func _any_busy() -> bool:
	for instrument in _instruments:
		if instrument.is_busy:
			return true
	return false

func _any_snapshot() -> bool:
	for instrument in _instruments:
		if instrument.has_active_snapshot:
			return true
	return false

func update_internal(dt: float) -> void:
	if is_destroyed or state == "Stopped":
		return
	var clocked_mutation = _update_offset != null
	if _update_offset != null:
		dt = maxf(0, dt - _update_offset)
	_update_offset = null
	var just_started = false
	var advance = dt
	if state == "Starting":
		if _start_delay > 0:
			_start_delay -= dt
			if _start_delay > 0:
				return
			advance = minf(dt, -_start_delay)
			_start_delay = 0
		clocked_mutation = clocked_mutation or _clocked_start
		_clocked_start = false
		state = "Playing"
		just_started = true
		if description.event.get("isConductor", false) and parent == null:
			system.conductor = self
		raise_callback(1, {"type": "StartedCallback"})
	if _seek_pending:
		_seek_pending = false
		if state == "Sustaining":
			state = "Playing"
		_timeline.reset(_seek_to)
		just_started = true
	var macros = description.event.get("macros", {})
	if description.is_3d:
		var listener = system.nearest_listener(attributes.position)
		for parameter in local_parameters.all:
			var builtin = parameter.definition.get("builtIn", "None")
			if builtin != "None":
				parameter.set_value(Spatial.compute(builtin, attributes, listener, macros.get("minDistance", 1), macros.get("maxDistance", 20)), true)
	local_parameters.advance(dt)
	var bpm = description.bpm_at(_timeline.position)
	var modulation = ModEval.advance(description.modulators, _mods, 0 if paused else dt, bpm)
	event_volume_db = macros.get("volumeDb", 0) + _volume_db + ModEval.evaluate_lanes(description.volume_lanes, self) + modulation.x + inherited_volume_db
	event_pitch = macros.get("pitch", 1) * _pitch * ModEval.evaluate_lanes(description.pitch_lanes, self, true) * modulation.y * inherited_pitch
	if just_started and can_trigger:
		_defer_native_seek = clocked_mutation
		if not _timeline.begin():
			on_timeline_jump(_timeline.position)
		_defer_native_seek = false
	if clocked_mutation and just_started:
		for instrument in _instruments:
			instrument.advance_initial_voices(advance)
	if not paused and can_trigger:
		_timeline.advance(advance)
	if can_trigger:
		_evaluate_parameter_instruments()
	for instrument in _instruments:
		instrument.update(dt, paused, bpm)
	if state == "Stopping":
		if not _any_busy():
			_finish()
	elif state == "Playing" and not macros.get("persistent", false) and not _timeline.holding and _timeline.transition == null and _timeline.position >= description.length and not _any_busy() and not _any_snapshot():
		_finish()

func emit_internal(commands: Array) -> bool:
	if is_destroyed:
		return false
	var any_real = false
	var count = 0
	for instrument in _instruments:
		any_real = instrument.emit_commands(commands, paused) or any_real
		count += instrument.voices.size() + instrument.children.size()
	var virtual_now = state != "Stopped" and (steal_virtual or (count > 0 and not any_real))
	if virtual_now != is_virtual:
		is_virtual = virtual_now
		raise_callback(64 if is_virtual else 128, {"type": "VirtualizedCallback" if is_virtual else "UnvirtualizedCallback"})
	return any_real

func _evaluate_parameter_instruments() -> void:
	for instrument in _instruments:
		if instrument.info.on_timeline or instrument.info.transition != null or instrument.sheet_parameter == null:
			continue
		var parameter = instrument.sheet_parameter
		var value = parameter.value
		var start = instrument.info.region_start
		var end = instrument.info.region_end
		var inside = value == start if end <= start else value >= start and (value < end or (value == end and end >= parameter.definition.get("maximum", 1)))
		if inside and not instrument.active:
			instrument.on_enter(0)
		elif not inside and instrument.active:
			instrument.on_exit()

func on_timeline_segment(from: float, to: float, inclusive: bool, looping_here: bool = false) -> void:
	for instrument in _instruments:
		var info = instrument.info
		if not info.on_timeline or info.transition != null:
			continue
		if info.is_point:
			if (info.region_start >= from if inclusive else info.region_start > from) and info.region_start <= to:
				instrument.on_point()
			continue
		var inside = to > info.region_start and to <= info.region_end if looping_here else to >= info.region_start and to < info.region_end
		if inside and not instrument.active:
			instrument.on_enter(to - info.region_start)
		elif not inside and instrument.active:
			instrument.on_exit()
		elif not inside and not instrument.active and not looping_here and (info.region_start >= from if inclusive else info.region_start > from) and info.region_end <= to:
			instrument.on_point()

func on_timeline_jump(position: float) -> void:
	for instrument in _instruments:
		var info = instrument.info
		if not info.on_timeline or info.transition != null:
			continue
		if info.is_point:
			if position == info.region_start:
				instrument.on_point()
			continue
		var inside = position >= info.region_start and position < info.region_end
		if inside and not instrument.active:
			instrument.on_enter(position - info.region_start)
		elif not inside and instrument.active:
			instrument.on_exit()
		elif inside and not info.instrument.get("async", false):
			instrument.on_jump(position, _defer_native_seek)

func on_transition_begin(marker: Dictionary) -> void:
	for instrument in _instruments:
		var info = instrument.info
		if not info.on_timeline:
			continue
		if info.transition == null:
			if instrument.active:
				instrument.on_exit()
		elif info.transition == marker:
			if info.is_point:
				if info.region_start == 0:
					instrument.on_point()
			elif info.region_start <= 0 and info.region_end > 0 and not instrument.active:
				instrument.on_enter(-info.region_start)

func on_transition_segment(marker: Dictionary, from: float, to: float) -> void:
	for instrument in _instruments:
		var info = instrument.info
		if info.transition != marker:
			continue
		if info.is_point:
			if info.region_start > from and info.region_start <= to:
				instrument.on_point()
			continue
		var inside = to >= info.region_start and to < info.region_end
		if inside and not instrument.active:
			instrument.on_enter(to - info.region_start)
		elif not inside and instrument.active:
			instrument.on_exit()

func on_transition_end(marker: Dictionary) -> void:
	for instrument in _instruments:
		if instrument.info.transition == marker and instrument.active:
			instrument.on_exit()

func conditions_pass(conditions: Array) -> bool:
	for condition in conditions:
		var parameter = resolve_parameter(condition.get("parameter", ""))
		if parameter == null or parameter.value < condition.get("minimum", 0) or parameter.value > condition.get("maximum", 0):
			return false
	return true

func marker_passes(marker: Dictionary) -> bool:
	return conditions_pass(marker.get("conditions", [])) and system.random.chance(marker.get("probability", 1.0))

func resolve_programmer_asset(key: String) -> String:
	var asset = system.resolve_table_asset(key)
	if not asset.is_empty():
		return asset
	var request = {"type": "ProgrammerSoundCallback", "key": key, "asset": ""}
	raise_callback(512, request)
	asset = system.asset_path(request.asset)
	if asset.is_empty():
		asset = _programmer_asset
	if system.recorder != null:
		system.recorder.programmer_sound(self, key, asset)
	return asset

func distance_to_nearest_listener() -> float:
	if not description.is_3d:
		return 0.0
	return attributes.position.distance_to(system.nearest_listener(attributes.position).position)

func raise_callback(kind: int, payload: Dictionary) -> void:
	if callback_mask & kind:
		payload["kind"] = kind
		callback.emit(self, payload)
