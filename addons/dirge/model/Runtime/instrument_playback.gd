@tool
extends RefCounted

const ModEval = preload("mod_eval.gd")
var info: Dictionary
var _instance_ref: WeakRef
var _system_ref: WeakRef
var instance:
	get: return _instance_ref.get_ref() if _instance_ref != null else null
	set(value): _instance_ref = weakref(value) if value != null else null
var system:
	get: return _system_ref.get_ref() if _system_ref != null else null
	set(value): _system_ref = weakref(value) if value != null else null
var active: bool = false
var voices: Array = []
var children: Array = []
var chain_volume_db: float = 0.0
var chain_pitch: float = 1.0
var sheet_parameter
var _mods: Array = []
var _delay_remaining: float = -1.0
var _pending_offset: float = 0.0
var _snapshot
var _recent: Array = []
var _shuffle: Array = []
var _shuffle_index: int = 0
var _sequential: int = 0
var _last_pick: int = -1
var _scattering: bool = false
var _scatter_timer: float = 0.0
var is_busy: bool:
	get: return not voices.is_empty() or not children.is_empty() or _delay_remaining >= 0 or _scattering
var has_active_snapshot: bool:
	get: return _snapshot != null and _snapshot.is_active

func _init(compiled: Dictionary, owner, root):
	info = compiled
	instance = owner
	system = root
	sheet_parameter = instance.resolve_parameter(info.instrument.get("placement", {}).get("parameter", "")) if not info.on_timeline else null

func reset() -> void:
	active = false
	_delay_remaining = -1
	_pending_offset = 0
	voices.clear()
	children.clear()
	_snapshot = null
	_recent.clear()
	_shuffle.clear()
	_shuffle_index = 0
	_sequential = 0
	_last_pick = -1
	_scattering = false
	_scatter_timer = 0
	chain_volume_db = 0
	chain_pitch = 1
	_mods.clear()

func on_enter(offset: float) -> void:
	if not instance.can_trigger:
		return
	active = true
	var instrument = info.instrument
	if not instance.conditions_pass(instrument.get("conditions", [])) or not system.random.chance(instrument.get("probability", 1.0)):
		return
	if instrument.get("triggerDelay", 0) > 0:
		_delay_remaining = instrument.triggerDelay
		_pending_offset = offset
	else:
		_fire(offset)

func on_point() -> void:
	on_enter(0)
	active = false

func on_exit() -> void:
	active = false
	if info.instrument.get("async", false):
		return
	_delay_remaining = -1
	if info.instrument.get("cutMode", "Cut") == "Cut" and _all_unplayed_oneshots():
		return
	_cut("Immediate" if info.instrument.get("cutMode", "Cut") == "Cut" else "AllowFadeout", info.instrument.get("fadeOut", 0))

func _all_unplayed_oneshots() -> bool:
	if voices.is_empty():
		return false
	for voice in voices:
		if voice.has_played or voice.loop:
			return false
	return true

func on_jump(position: float, defer_seek: bool = false) -> void:
	var offset = info.instrument.get("startOffset", 0) + position - info.region_start
	for voice in voices:
		if voice.finished:
			continue
		voice.position = fmod(offset, voice.length) if voice.loop and voice.length_known and voice.length > 0 else offset
		if voice.is_real and voice.has_played:
			if defer_seek:
				voice.wants_seek = true
			else:
				system.commands.append({"type": "VoiceSeek", "voice": voice.id, "seconds": voice.position})

func advance_initial_voices(elapsed: float) -> void:
	for voice in voices:
		if voice.fresh:
			voice.initial_advance = elapsed

func stop(mode: String, event_release: float = 0.0) -> void:
	_delay_remaining = -1
	_cut(mode, maxf(info.instrument.get("fadeOut", 0), maxf(info.max_release, event_release)))

func key_off() -> void:
	ModEval.release(info.modulators, _mods)

func _cut(mode: String, fade: float) -> void:
	_scattering = false
	if mode != "Immediate":
		key_off()
	for voice in voices.duplicate():
		if mode == "Immediate" or fade <= 0:
			_stop_voice(voice)
		elif not voice.fading_out or voice.fade_out_remaining > fade:
			voice.fading_out = true
			voice.fade_out_duration = fade
			voice.fade_out_remaining = fade
	for child in children:
		child.stop(mode)
	_reap_children()
	if _snapshot != null:
		_snapshot.stop()
		_snapshot = null

func _fire(offset: float) -> void:
	_mods = ModEval.initialize(info.modulators, instance, system.random)
	var instrument = info.instrument
	match info.kind:
		"Clip": _start_voice(instrument.get("asset", ""), offset)
		"Multi":
			var entries = instrument.get("entries", [])
			var pick = _pick(entries, instrument.get("mode", "Random"), instrument.get("avoidRepeat", 1))
			if pick >= 0:
				_start_voice(entries[pick].get("asset", ""), offset)
		"Scatterer":
			_scattering = true
			_scatter_timer = 0
		"Event": _start_child()
		"Programmer": _start_voice(instance.resolve_programmer_asset(instrument.get("key", "")), offset)
		"Command": system.execute_command(instrument, instance)
		"Snapshot":
			if _snapshot != null:
				_snapshot.stop()
			for snapshot in system.project.get("snapshots", []):
				if snapshot.get("id", "") == instrument.get("snapshot", ""):
					_snapshot = system.mixer.start_snapshot(snapshot.get("name", ""))
					break

func _start_child() -> void:
	var nested = system.get_event(info.instrument.get("event", ""))
	if nested == null or instance.depth >= 8:
		return
	var polyphony = info.instrument.get("polyphony", 0)
	while polyphony > 0 and children.size() >= polyphony:
		var oldest = children.pop_front()
		oldest.stop("Immediate")
		oldest.destroy()
	var child = nested.create_instance(instance)
	child.set_3d_attributes(instance.attributes)
	child.inherited_volume_db = instance.event_volume_db + chain_volume_db
	child.inherited_pitch = instance.event_pitch * chain_pitch
	child.start()
	if child.state == "Stopped":
		child.destroy()
	else:
		children.append(child)

func _start_voice(asset, offset: float, position = null) -> void:
	var path = system.asset_path(asset)
	if path.is_empty():
		return
	var instrument = info.instrument
	var macros = instance.description.event.get("macros", {})
	var start = instrument.get("startOffset", 0) + offset
	var length = system.asset_length(path)
	if length > 0:
		if instrument.get("loop", false):
			start = fmod(start, length)
		elif start >= length:
			return
	var polyphony = instrument.get("polyphony", 0)
	while polyphony > 0 and voices.size() >= polyphony:
		_stop_voice(voices[0])
	var voice = system.voices.acquire()
	voice.instance = instance
	voice.owner = self
	voice.asset = path
	voice.bus = instance.description.bus_name
	voice.bus_index = instance.description.bus_index
	voice.loop = instrument.get("loop", false)
	voice.spatial = instance.description.is_3d
	voice.attributes = instance.attributes.duplicate()
	if position != null:
		voice.attributes.position = position
		voice.own_attributes = true
	voice.min_distance = macros.get("minDistance", 1)
	voice.max_distance = macros.get("maxDistance", 20)
	voice.priority = macros.get("priority", 128)
	voice.position = start
	voice.length = length
	voice.length_known = length > 0
	if instrument.get("volumeRandomDb", 0) != 0:
		voice.random_volume_db = system.random.next_signed() * instrument.volumeRandomDb
	if instrument.get("pitchRandom", 0) != 0:
		voice.random_pitch = maxf(0.01, 1 + system.random.next_signed() * instrument.pitchRandom)
	voice.fade_in = instrument.get("fadeIn", 0)
	voice.volume_db = instance.event_volume_db + chain_volume_db + voice.random_volume_db + voice.fade_db()
	voice.pitch = instance.event_pitch * chain_pitch * voice.random_pitch
	voices.append(voice)

func _stop_voice(voice) -> void:
	if voice.is_real and voice.has_played:
		system.commands.append({"type": "VoiceStop", "voice": voice.id})
	finish_voice(voice)

func finish_voice(voice) -> void:
	if not voices.has(voice):
		return
	voices.erase(voice)
	instance.raise_callback(32, {"type": "SoundStoppedCallback", "voice": voice.id})
	system.voices.release(voice)

func _pick(entries: Array, mode: String, avoid_repeat: int) -> int:
	var n = entries.size()
	if n == 0:
		return -1
	var pick = -1
	match mode:
		"Sequential":
			for k in range(n):
				var i = (_sequential + k) % n
				if entries[i].get("weight", 1) > 0:
					pick = i
					_sequential = (i + 1) % n
					break
		"Shuffle":
			for _attempt in range(2):
				if _shuffle_index >= _shuffle.size():
					_shuffle = range(n)
					for i in range(n - 1, 0, -1):
						var j = system.random.next_int(i + 1)
						var temp = _shuffle[i]
						_shuffle[i] = _shuffle[j]
						_shuffle[j] = temp
					if n > 1 and _shuffle[0] == _last_pick:
						var j = 1 + system.random.next_int(n - 1)
						var temp = _shuffle[0]
						_shuffle[0] = _shuffle[j]
						_shuffle[j] = temp
					_shuffle_index = 0
				while _shuffle_index < n:
					var i = _shuffle[_shuffle_index]
					_shuffle_index += 1
					if entries[i].get("weight", 1) > 0:
						pick = i
						break
				if pick >= 0:
					break
		_:
			var window = clampi(avoid_repeat, 0, n - 1)
			var total = 0.0
			for i in range(n):
				if entries[i].get("weight", 1) > 0 and not _recent.has(i):
					total += entries[i].get("weight", 1)
			var ignore_recent = total <= 0 or window == 0
			if ignore_recent:
				total = 0
				for entry in entries:
					total += maxf(0, entry.get("weight", 1))
			if total <= 0:
				return -1
			var choice = system.random.next_float() * total
			for i in range(n):
				var weight = entries[i].get("weight", 1)
				if weight <= 0 or (not ignore_recent and _recent.has(i)):
					continue
				pick = i
				choice -= weight
				if choice < 0:
					break
			if pick >= 0 and window > 0:
				_recent.append(pick)
				while _recent.size() > window:
					_recent.pop_front()
	if pick >= 0:
		_last_pick = pick
	return pick

func update(dt: float, paused: bool, bpm: float) -> void:
	var instrument = info.instrument
	var modulation = ModEval.advance(info.modulators, _mods, 0 if paused else dt, bpm)
	chain_volume_db = instrument.get("volumeDb", 0) + info.track.get("volumeDb", 0) + ModEval.evaluate_lanes(info.volume_lanes, instance) + modulation.x
	chain_pitch = instrument.get("pitch", 1) * ModEval.evaluate_lanes(info.pitch_lanes, instance, true) * modulation.y
	if not paused:
		if _delay_remaining >= 0:
			_delay_remaining -= dt
			if _delay_remaining <= 0:
				var late = -_delay_remaining
				_delay_remaining = -1
				if (active or instrument.get("async", false)) and instance.can_trigger:
					_fire(_pending_offset + late)
		if _scattering:
			_scatter_timer -= dt
			for _guard in range(64):
				if _scatter_timer > 0:
					break
				_spawn()
				var minimum = maxf(0, instrument.get("minInterval", 0.5))
				var maximum = maxf(minimum, instrument.get("maxInterval", 2))
				_scatter_timer += minimum + system.random.next_float() * (maximum - minimum)
				if maximum <= 0:
					_scatter_timer = INF
					break
	for voice in voices.duplicate():
		if voice.finished:
			finish_voice(voice)
			continue
		var voice_dt = dt
		var advance_voice = not voice.fresh or voice.initial_advance > 0
		if voice.fresh:
			voice_dt = voice.initial_advance
			voice.initial_advance = 0
			voice.fresh = false
		if not paused and advance_voice:
			voice.age += voice_dt
			voice.position += voice_dt * voice.pitch
			if voice.length_known:
				if voice.loop:
					voice.position = fmod(voice.position, voice.length)
				elif voice.position >= voice.length:
					_stop_voice(voice)
					continue
			if voice.fading_out:
				voice.fade_out_remaining -= voice_dt
				if voice.fade_out_remaining <= 0:
					_stop_voice(voice)
					continue
		voice.volume_db = instance.event_volume_db + chain_volume_db + voice.random_volume_db + voice.fade_db()
		voice.pitch = instance.event_pitch * chain_pitch * voice.random_pitch
		if voice.spatial and not voice.own_attributes:
			voice.attributes = instance.attributes.duplicate()
	for child in children:
		child.inherited_volume_db = instance.event_volume_db + chain_volume_db
		child.inherited_pitch = instance.event_pitch * chain_pitch
		child.set_3d_attributes(instance.attributes)
		child.update_internal(dt)
	_reap_children()

func _spawn() -> void:
	var instrument = info.instrument
	var entries = instrument.get("entries", [])
	var pick = _pick(entries, instrument.get("mode", "Random"), 1)
	if pick < 0:
		return
	var minimum = maxf(0, instrument.get("minScatterDistance", 0))
	var maximum = maxf(minimum, instrument.get("maxScatterDistance", 10))
	var distance = minimum + system.random.next_float() * (maximum - minimum)
	var angle = system.random.next_float() * TAU
	var center = instance.attributes.position
	_start_voice(entries[pick].get("asset", ""), 0, center + Vector3(cos(angle) * distance, 0, sin(angle) * distance))

func _reap_children() -> void:
	for child in children.duplicate():
		if child.state == "Stopped":
			children.erase(child)
			child.destroy()

func emit_commands(commands: Array, paused: bool) -> bool:
	var any_real = false
	# C# ownership is newest-first; preserve command ordering for the oracle.
	for i in range(voices.size() - 1, -1, -1):
		var voice = voices[i]
		if voice.finished or not voice.is_real:
			continue
		any_real = true
		if voice.wants_play:
			var macros = instance.description.event.get("macros", {})
			commands.append({"type": "VoicePlay", "voice": voice.id, "asset": voice.asset, "bus": voice.bus,
				"startSeconds": voice.position, "loop": voice.loop, "volumeDb": voice.volume_db, "pitch": voice.pitch,
				"spatial": voice.spatial, "attributes": voice.attributes.duplicate(), "minDistance": voice.min_distance,
				"maxDistance": voice.max_distance, "syncGroup": info.instrument.get("syncGroup", 0),
				"rolloff": macros.get("rolloff", "Inverse"), "doppler": macros.get("doppler", false), "dopplerScale": macros.get("dopplerScale", 1)})
			voice.wants_play = false
			voice.wants_seek = false
			voice.has_played = true
			voice.last_volume_db = voice.volume_db
			voice.last_pitch = voice.pitch
			voice.last_attributes = voice.attributes.duplicate()
			voice.last_paused = paused
			if paused:
				commands.append({"type": "VoiceSetPaused", "voice": voice.id, "paused": true})
			instance.raise_callback(16, {"type": "SoundPlayedCallback", "voice": voice.id, "asset": voice.asset})
			continue
		if voice.wants_seek:
			commands.append({"type": "VoiceSeek", "voice": voice.id, "seconds": voice.position})
			voice.wants_seek = false
		if voice.volume_db != voice.last_volume_db:
			commands.append({"type": "VoiceSetVolume", "voice": voice.id, "volumeDb": voice.volume_db})
			voice.last_volume_db = voice.volume_db
		if voice.pitch != voice.last_pitch:
			commands.append({"type": "VoiceSetPitch", "voice": voice.id, "pitch": voice.pitch})
			voice.last_pitch = voice.pitch
		if voice.spatial and voice.attributes != voice.last_attributes:
			commands.append({"type": "VoiceSet3D", "voice": voice.id, "attributes": voice.attributes.duplicate()})
			voice.last_attributes = voice.attributes.duplicate()
		if paused != voice.last_paused:
			commands.append({"type": "VoiceSetPaused", "voice": voice.id, "paused": paused})
			voice.last_paused = paused
	for child in children:
		any_real = child.emit_internal(commands) or any_real
	return any_real
