@tool
extends RefCounted

class Voice:
	extends RefCounted
	var id: int
	var _instance_ref: WeakRef
	var _owner_ref: WeakRef
	var instance:
		get: return _instance_ref.get_ref() if _instance_ref != null else null
		set(value): _instance_ref = weakref(value) if value != null else null
	var owner:
		get: return _owner_ref.get_ref() if _owner_ref != null else null
		set(value): _owner_ref = weakref(value) if value != null else null
	var asset: String = ""
	var bus: String = "Master"
	var bus_index: int = 0
	var loop: bool = false
	var spatial: bool = false
	var attributes: Dictionary = {}
	var own_attributes: bool = false
	var min_distance: float = 1.0
	var max_distance: float = 20.0
	var priority: int = 128
	var position: float = 0.0
	var length: float = 0.0
	var length_known: bool = false
	var age: float = 0.0
	var random_volume_db: float = 0.0
	var random_pitch: float = 1.0
	var fade_in: float = 0.0
	var fade_out_duration: float = 0.0
	var fade_out_remaining: float = 0.0
	var fading_out: bool = false
	var is_real: bool = false
	var wants_play: bool = false
	var has_played: bool = false
	var wants_seek: bool = false
	var finished: bool = false
	var fresh: bool = true
	var initial_advance: float = 0.0
	var volume_db: float = 0.0
	var pitch: float = 1.0
	var last_volume_db: float = 0.0
	var last_pitch: float = 1.0
	var last_attributes: Dictionary = {}
	var last_paused: bool = false

	func fade_db() -> float:
		var linear = 1.0
		if fade_in > 0 and age < fade_in:
			linear *= age / fade_in
		if fading_out:
			linear *= clampf(fade_out_remaining / fade_out_duration, 0, 1) if fade_out_duration > 0 else 0.0
		return maxf(-80.0, linear_to_db(linear)) if linear > 0 else -80.0

var active: Array = []
var real_count: int = 0
var _by_id: Dictionary = {}
var _free_ids: Array = []
var _next_id: int = 0
var _bus_max: Array = []

func _init(mixer: Dictionary):
	for bus in mixer.get("buses", []):
		_bus_max.append(maxi(0, bus.get("maxVoices", 0)))
	if _bus_max.is_empty():
		_bus_max.append(0)

func acquire():
	var voice = Voice.new()
	if _free_ids.is_empty():
		voice.id = _next_id
		_next_id += 1
	else:
		voice.id = _free_ids.pop_front()
	_by_id[voice.id] = voice
	active.append(voice)
	return voice

func release(voice) -> void:
	active.erase(voice)
	_by_id.erase(voice.id)
	_free_ids.append(voice.id)
	if voice.is_real:
		real_count = maxi(0, real_count - 1)
	voice.owner = null
	voice.instance = null

func find(id: int):
	return _by_id.get(id)

func apply_limits(max_real: int, commands: Array) -> void:
	var candidates: Array = []
	for voice in active:
		if voice.finished:
			continue
		if voice.instance.steal_virtual:
			_set_real(voice, false, commands)
		else:
			candidates.append(voice)
	candidates.sort_custom(func(a, b):
		var left = a.priority * 1000000.0 - minf(a.age, 999999.0)
		var right = b.priority * 1000000.0 - minf(b.age, 999999.0)
		return left < right if left != right else a.id < b.id)
	var counts: Array = []
	counts.resize(_bus_max.size())
	counts.fill(0)
	real_count = 0
	for voice in candidates:
		var bus = clampi(voice.bus_index, 0, counts.size() - 1)
		var admit = real_count < max_real and (_bus_max[bus] == 0 or counts[bus] < _bus_max[bus])
		if admit:
			real_count += 1
			counts[bus] += 1
		_set_real(voice, admit, commands)

func _set_real(voice, real: bool, commands: Array) -> void:
	if voice.is_real == real:
		return
	voice.is_real = real
	voice.wants_play = real
	if not real:
		if voice.has_played:
			commands.append({"type": "VoiceStop", "voice": voice.id})
		voice.has_played = false
		voice.wants_seek = false
		if not voice.loop and not voice.length_known:
			voice.finished = true
