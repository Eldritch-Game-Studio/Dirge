@tool
extends RefCounted

const Bank = preload("parameter_bank.gd")
const Description = preload("event_description.gd")
const VoicePool = preload("voice_pool.gd")
const Mixer = preload("mixer_state.gd")
const Spatial = preload("spatial.gd")
const Modulators = preload("../Evaluation/modulators.gd")
const MAX_LISTENERS = 8

var project: Dictionary
var mixer
var global_parameters
var commands: Array = []
var time: float = 0.0
var instances: Array = []
var instance_count: int = 0
var real_voice_count: int:
	get: return voices.real_count
var seed: int
var platform: String
var assets
var random
var voices
var conductor
var recorder
var elapsed_since_last_update: Callable
var max_real_voices: int = 64:
	set(value):
		_record("SetMaxRealVoices", null, null, value)
		max_real_voices = value
var locale: String = "":
	set(value):
		_record("SetLocale", value)
		locale = value
var _descriptions: Array = []
var _lookup: Dictionary = {}
var _tables: Dictionary = {}
var _listeners: Array = []
var _weights: Array = []
var _destroy_queue: Array = []
var _feedback: Array = []
var _updating: bool = false

func _init(authored: Dictionary, random_seed: int = 0, catalog = null, platform_name: String = "", clock: Callable = Callable()):
	project = authored
	seed = random_seed
	platform = platform_name
	assets = catalog
	elapsed_since_last_update = clock
	random = Modulators.DirgeRandom.new(seed)
	global_parameters = Bank.new(project.get("globalParameters", []))
	voices = VoicePool.new(project.get("mixer", {}))
	for i in range(MAX_LISTENERS):
		_listeners.append(Spatial.origin())
		_weights.append(1.0 if i == 0 else 0.0)
	for event in project.get("events", []):
		var description = Description.new(self, event)
		_descriptions.append(description)
		_lookup[description.id] = description
		_lookup[description.path] = description
	for table in project.get("audioTables", []):
		for key in table.get("entries", {}):
			if not _tables.has(key):
				_tables[key] = table.entries[key]
	mixer = Mixer.new(project, global_parameters, commands, random, platform)
	mixer.system = self

static func asset_path(asset) -> String:
	return str(asset.get("path", "")) if asset is Dictionary else ("" if asset == null else str(asset))

func asset_length(asset) -> float:
	var path = asset_path(asset)
	if assets != null:
		if assets is Dictionary:
			return float(assets.get(path, 0))
		if assets.has_method("get_length"):
			return float(assets.get_length(path))
		if assets.has_method("try_get_length"):
			return float(assets.try_get_length(path))
	return float(project.get("assetLengths", {}).get(path, 0))

func get_event(path_or_id: String):
	return _lookup.get(path_or_id)

func _record(method: String, name = null, text = null, value: float = 0, flag: bool = false, mode: int = 0) -> void:
	if recorder != null:
		recorder.record_call(null, method, name, text, value, flag, mode)

func enter_internal() -> void:
	if recorder != null:
		recorder.enter_internal()

func exit_internal() -> void:
	if recorder != null:
		recorder.exit_internal()

func mutation_update_offset():
	return elapsed_since_last_update.call() if not _updating and elapsed_since_last_update.is_valid() else null

func set_global_parameter(name: String, value: float, immediate: bool = false) -> void:
	_record("SetGlobalParameter", name, null, value, immediate)
	global_parameters.set_value(name, value, immediate)

func get_global_parameter(name: String) -> float:
	return global_parameters.get_value(name)

func set_listener(index: int, attributes: Dictionary, weight: float = 1.0) -> void:
	if index < 0 or index >= MAX_LISTENERS:
		push_error("Dirge listener index must be in 0..7")
		return
	_listeners[index] = Spatial.attributes(attributes)
	_weights[index] = maxf(0, weight)
	if recorder != null:
		recorder.call_3d(null, "SetListener", index, _listeners[index], weight)

func nearest_listener(position: Vector3) -> Dictionary:
	var best = 0
	var distance = INF
	for i in range(MAX_LISTENERS):
		if _weights[i] <= 0:
			continue
		var candidate = position.distance_squared_to(_listeners[i].position)
		if candidate < distance:
			distance = candidate
			best = i
	return _listeners[best]

func resolve_table_asset(key: String) -> String:
	var locales = _tables.get(key, {})
	if locales.is_empty():
		return ""
	return asset_path(locales[locale] if not locale.is_empty() and locales.has(locale) else locales.values()[0])

func on_conductor_stopped(instance) -> void:
	if conductor != instance:
		return
	conductor = null
	var best = -INF
	for candidate in instances:
		if candidate.description.event.get("isConductor", false) and candidate.can_trigger and candidate.start_time >= best:
			conductor = candidate
			best = candidate.start_time

func stop_all(mode: String = "AllowFadeout") -> void:
	_record("StopAll", null, null, 0, false, 1 if mode == "Immediate" else 0)
	enter_internal()
	for i in range(instances.size() - 1, -1, -1):
		if i < instances.size():
			instances[i].stop(mode)
	exit_internal()

func feedback(message: Dictionary) -> void:
	if message.is_empty():
		return
	_record("Feedback", message.get("type", ""), message.get("reason"), message.get("voice", 0))
	_feedback.append(message)

func request_destroy(instance) -> void:
	if not _destroy_queue.has(instance):
		_destroy_queue.append(instance)

func _flush_destroy_queue() -> void:
	for instance in _destroy_queue:
		if not instance.is_destroyed:
			instances.erase(instance)
			instance.destroy()
	_destroy_queue.clear()

func execute_command(command: Dictionary, source) -> void:
	match command.get("action", "StartEvent"):
		"StartEvent":
			var target = get_event(command.get("targetEvent", ""))
			if target != null:
				var instance = target.create_instance()
				instance.start()
				instance.release()
		"StopEvent":
			var target = get_event(command.get("targetEvent", ""))
			if target != null:
				target.stop_all()
		"SetParameter":
			var parameter = source.resolve_parameter(command.get("targetParameter", ""))
			if parameter != null:
				parameter.set_value(command.get("value", 0))

func update(dt: float) -> void:
	dt = maxf(0, dt)
	_updating = true
	if recorder != null:
		recorder.begin_update(self, dt)
	enter_internal()
	_flush_destroy_queue()
	# Swap queues: feedback produced by callbacks is deferred to the next tick as well.
	var pending = _feedback
	_feedback = []
	for message in pending:
		var voice = voices.find(message.get("voice", -1))
		if voice != null and (message.get("type", "") == "VoiceFailed" or (message.get("type", "") == "VoiceFinished" and not voice.length_known)):
			voice.owner.finish_voice(voice)
	global_parameters.advance(dt)
	var count = instances.size()
	for i in range(count):
		if i < instances.size():
			instances[i].update_internal(dt)
	for description in _descriptions:
		description.apply_virtualize_stealing()
	voices.apply_limits(maxi(0, max_real_voices), commands)
	count = instances.size()
	for i in range(count):
		if i < instances.size():
			instances[i].emit_internal(commands)
	mixer.update(dt)
	time += dt
	_flush_destroy_queue()
	exit_internal()
	if recorder != null:
		recorder.end_update(self)
	_updating = false
