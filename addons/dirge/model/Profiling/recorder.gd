@tool
extends RefCounted

const Recording = preload("recording.gd")
const CommandText = preload("command_text.gd")

var recording: Dictionary
var is_attached: bool = false
var _system_ref: WeakRef
var _system:
	get:
		return _system_ref.get_ref() if _system_ref != null else null
	set(value):
		_system_ref = weakref(value) if value != null else null
var _handles: Dictionary = {}
var _next_handle: int = 0
var _internal_depth: int = 0
var _pending: Dictionary = Recording.tick()
var _current = null

func _init(system) -> void:
	assert(system != null and system.recorder == null, "System already has a recorder")
	_system = system
	recording = Recording.create(system.project.get("name", ""), system.seed, system.platform, system.locale)
	system.recorder = self
	is_attached = true

func stop() -> Dictionary:
	if is_attached:
		if _system != null and _system.recorder == self:
			_system.recorder = null
		is_attached = false
	return recording

func handle_of(target) -> int:
	return _handles[target].handle if _handles.has(target) else -1

func _kind(target) -> String:
	if target == null or target == _system:
		return "System"
	if _system != null and target == _system.mixer:
		return "Mixer"
	if target.has_method("activate") and target.has_method("deactivate"):
		return "Snapshot"
	return "Instance"

func created(target, method: String, name: String) -> void:
	if _internal_depth > 0 or not is_attached:
		return
	var kind: String = _kind(target)
	var handle: int = _next_handle
	_next_handle += 1
	_handles[target] = {"handle": handle, "live": kind == "Snapshot", "kind": kind}
	var call: Dictionary = Recording.api_call(method, kind)
	call.name = name
	call.result = handle
	_pending.calls.append(call)

func record_call(target, method: String, name = null, text = null, value: float = 0.0, flag: bool = false, mode: int = 0) -> void:
	if _internal_depth > 0 or not is_attached:
		return
	var kind: String = _kind(target)
	var handle: int = handle_of(target)
	if handle < 0 and kind in ["Instance", "Snapshot"]:
		return
	var call: Dictionary = Recording.api_call(method, kind)
	call.merge({"handle": handle, "name": name, "text": text, "value": value, "flag": flag, "mode": mode}, true)
	_pending.calls.append(call)

func call_3d(target, method: String, index: int, attributes: Dictionary, weight: float) -> void:
	if _internal_depth > 0 or not is_attached:
		return
	var kind: String = _kind(target)
	var handle: int = handle_of(target)
	if handle < 0 and kind == "Instance":
		return
	var call: Dictionary = Recording.api_call(method, kind)
	call.merge({"handle": handle, "mode": index, "value": weight, "vector": Recording.pack(attributes)}, true)
	_pending.calls.append(call)

func programmer_sound(instance, key: String, asset) -> void:
	if not is_attached:
		return
	var tick: Dictionary = _current if _current != null else _pending
	tick.programmerSounds.append({"handle": handle_of(instance), "key": key, "asset": asset.get("path", "") if asset is Dictionary else str(asset)})

func enter_internal() -> void:
	_internal_depth += 1

func exit_internal() -> void:
	_internal_depth = maxi(0, _internal_depth - 1)

func begin_update(system, dt: float) -> void:
	_current = _pending
	_current.dt = dt
	_current.time = system.time
	_pending = Recording.tick()

func end_update(system) -> void:
	var tick: Dictionary = _current if _current != null else _pending
	for command in system.commands:
		tick.commands.append(CommandText.format(command))
	tick.instanceCount = system.instance_count
	tick.realVoiceCount = system.real_voice_count
	var ended: Array = []
	for target in _handles:
		var tracked: Dictionary = _handles[target]
		var live: bool
		var forget: bool
		if tracked.kind == "Snapshot":
			live = target.is_active
			forget = not live
		else:
			live = target.state != "Stopped"
			forget = target.is_destroyed
		if tracked.live and not live:
			tick.ended.append(tracked.handle)
		tracked.live = live
		if forget:
			ended.append(target)
	for target in ended:
		_handles.erase(target)
	recording.ticks.append(tick)
	_current = null
