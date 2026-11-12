@tool
extends Node

signal feedback(message: Dictionary)
signal voice_finished(voice: int)
signal sync_group_started(group: int, layers: int)

const Catalog = preload("res://addons/dirge/runtime/asset_catalog.gd")
const NONE_ID = "00000000000000000000000000000000"
const MAX_SYNC_STREAMS = 32
static var verbose: bool = false
var assets
var bus_names: Array[String] = []
var capture:
	set(value):
		if capture == value:
			return
		if capture != null:
			capture.uninstall()
		capture = value
		if capture != null:
			capture.install()
var _indices: Dictionary = {}
var _names: Dictionary = {}
var _effects: Dictionary = {}
var _faders: Dictionary = {}
var _voices: Dictionary = {}
var _all: Array = []
var _free: Array = [[], [], [], []]

func _init(catalog = null) -> void:
	assets = catalog if catalog != null else Catalog.new()
	name = "AudioDriver"

func load(project: Dictionary) -> bool:
	return load_mixer(project.get("mixer", {}))

func load_mixer(mixer: Dictionary) -> bool:
	# Native effects are required, never replaced by a disabled or fake effect.
	for bus in mixer.get("buses", []):
		for authored in bus.get("effects", []):
			var type_name = authored.get("typeName", "")
			if type_name.begins_with("Dirge") and (not ClassDB.class_exists(type_name) or not ClassDB.can_instantiate(type_name)):
				push_error("Dirge: required native effect '%s' is unavailable." % type_name)
				return false
	for index in range(AudioServer.bus_count - 1, 0, -1):
		AudioServer.remove_bus(index)
	for index in range(AudioServer.get_bus_effect_count(0) - 1, -1, -1):
		AudioServer.remove_bus_effect(0, index)
	_indices.clear()
	_names.clear()
	_effects.clear()
	_faders.clear()
	bus_names.clear()
	var master: Dictionary = {}
	for bus in mixer.get("buses", []):
		if bus.get("parent", NONE_ID) in ["", NONE_ID]:
			master = bus
			break
	if master.is_empty():
		push_error("Dirge: mixer graph has no master bus.")
		AudioServer.set_bus_name(0, "Master")
		_indices["Master"] = 0
		_names["Master"] = &"Master"
		bus_names.append("Master")
		if capture != null:
			capture.install()
		return false
	AudioServer.set_bus_name(0, master.get("name", "Master"))
	_configure_bus(0, master)
	var pending: Array = []
	for order in range(3):
		for bus in mixer.get("buses", []):
			if bus.get("id") != master.get("id") and _send_order(bus) == order:
				pending.append(bus)
	var placed = {master.get("id", NONE_ID): 0}
	var progress = true
	while not pending.is_empty() and progress:
		progress = false
		for bus in pending.duplicate():
			if not placed.has(bus.get("parent", NONE_ID)):
				continue
			var index = AudioServer.bus_count
			AudioServer.add_bus(index)
			AudioServer.set_bus_name(index, bus.get("name", "Bus"))
			AudioServer.set_bus_send(index, AudioServer.get_bus_name(placed[bus.parent]))
			_configure_bus(index, bus)
			placed[bus.id] = index
			pending.erase(bus)
			progress = true
	for bus in pending:
		push_error("Dirge: bus '%s' has an unreachable parent." % bus.get("name", ""))
	for bus in mixer.get("buses", []):
		for effect in bus.get("effects", []):
			if effect.get("typeName", "") == "DirgeAudioEffectSend":
				var target = effect.get("stringParameters", {}).get("target_bus", "")
				if bus_index(target) < 0:
					push_warning("Dirge: unknown send target '%s'." % target)
				elif bus_index(target) >= bus_index(bus.name):
					push_warning("Dirge: return '%s' mixes before its sender; send is one mix block late." % target)
	if capture != null:
		capture.install()
	return pending.is_empty()

func _send_order(bus: Dictionary) -> int:
	var order = 1
	for effect in bus.get("effects", []):
		if effect.get("typeName", "") == "DirgeAudioEffectReturn":
			return 0
		if effect.get("typeName", "") == "DirgeAudioEffectSend":
			order = 2
	return order

func _configure_bus(index: int, bus: Dictionary) -> void:
	var bus_name: String = bus.get("name", "Master")
	_indices[bus_name] = index
	_names[bus_name] = AudioServer.get_bus_name(index)
	bus_names.append(bus_name)
	var chain: Array = []
	var authored_chain: Array = bus.get("effects", [])
	var fader_index = int(bus.get("faderIndex", 2147483647))
	var bypass: bool = bus.get("bypassEffects", false)
	if fader_index >= 0 and fader_index < authored_chain.size():
		var fader = AudioEffectAmplify.new()
		fader.volume_db = bus.get("volumeDb", 0.0)
		_faders[bus_name] = {"effect": fader, "index": fader_index, "bypassed": bypass}
	AudioServer.set_bus_volume_db(index, bus.get("volumeDb", 0.0) if not _faders.has(bus_name) or bypass else 0.0)
	AudioServer.set_bus_mute(index, bus.get("mute", false))
	AudioServer.set_bus_solo(index, bus.get("solo", false))
	AudioServer.set_bus_bypass_effects(index, bypass)
	for authored_index in range(authored_chain.size()):
		if _faders.has(bus_name) and authored_index == fader_index:
			AudioServer.add_bus_effect(index, _faders[bus_name].effect)
		var authored: Dictionary = authored_chain[authored_index]
		var type_name: String = authored.get("typeName", "")
		var effect: AudioEffect = null
		if ClassDB.class_exists(type_name) and ClassDB.is_parent_class(type_name, "AudioEffect") and ClassDB.can_instantiate(type_name):
			effect = ClassDB.instantiate(type_name) as AudioEffect
		if effect == null:
			push_error("Dirge: '%s' is not an instantiable AudioEffect; slot disabled." % type_name)
			effect = AudioEffectAmplify.new()
			AudioServer.add_bus_effect(index, effect)
			AudioServer.set_bus_effect_enabled(index, engine_effect_index(bus_name, authored_index), false)
		else:
			for parameter in authored.get("parameters", {}):
				effect.set(parameter, authored.parameters[parameter])
			for parameter in authored.get("stringParameters", {}):
				var value = authored.stringParameters[parameter]
				# The native convolver exposes an AudioStream resource, not a path property.
				if type_name == "DirgeAudioEffectConvolutionReverb" and parameter == "impulse":
					value = assets.get_stream(value) if not str(value).is_empty() else null
				effect.set(parameter, value)
			AudioServer.add_bus_effect(index, effect)
			AudioServer.set_bus_effect_enabled(index, engine_effect_index(bus_name, authored_index), authored.get("enabled", true))
		chain.append(effect)
	_effects[bus_name] = chain

func bus_index(bus: String) -> int:
	return _indices.get(bus, -1)

func effects_of(bus: String) -> Array:
	return _effects.get(bus, [])

func fader_of(bus: String) -> AudioEffectAmplify:
	return _faders[bus].effect if _faders.has(bus) else null

func engine_effect_index(bus: String, authored_index: int) -> int:
	return authored_index + 1 if _faders.has(bus) and authored_index >= _faders[bus].index else authored_index

func set_effect_parameter(bus: String, index: int, parameter: String, value) -> bool:
	var chain = effects_of(bus)
	if index < 0 or index >= chain.size():
		return false
	if parameter == "impulse" and chain[index].is_class("DirgeAudioEffectConvolutionReverb") and value is String:
		value = assets.get_stream(value) if not value.is_empty() else null
	chain[index].set(parameter, value)
	return true

func player_of(voice: int) -> Node:
	return _voices[voice].slot.player if _voices.has(voice) else null

func is_voice_playing(voice: int) -> bool:
	var player = player_of(voice)
	return player != null and player.playing

func is_synchronized(voice: int) -> bool:
	return _voices.has(voice) and _voices[voice].slot.sync != null

func apply(commands) -> void:
	if commands is Dictionary:
		_apply_one(commands)
		return
	var consumed: Dictionary = {}
	for i in range(commands.size()):
		if consumed.has(i):
			continue
		var command: Dictionary = commands[i]
		if command.get("type") == "VoicePlay" and command.get("syncGroup", 0) != 0:
			var group: Array = [command]
			for j in range(i + 1, commands.size()):
				var other: Dictionary = commands[j]
				if group.size() == MAX_SYNC_STREAMS:
					break
				if not consumed.has(j) and other.get("type") == "VoicePlay" and other.get("syncGroup", 0) == command.syncGroup and other.get("spatial", false) == command.get("spatial", false):
					group.append(other)
					consumed[j] = true
			if group.size() > 1:
				_play_group(group)
			else:
				_play(command)
		else:
			_apply_one(command)

func _apply_one(command: Dictionary) -> void:
	var voice = int(command.get("voice", -1))
	var player = player_of(voice)
	var bus: String = command.get("bus", "")
	var index = bus_index(bus)
	match command.get("type", ""):
		"VoicePlay": _play(command)
		"VoiceStop": _release(voice)
		"VoiceSeek":
			if player != null: player.seek(command.seconds)
		"VoiceSetVolume":
			if player != null:
				var binding: Dictionary = _voices[voice]
				if binding.slot.sync != null:
					binding.slot.sync.set_sync_stream_volume(binding.index, command.volumeDb)
				else: player.volume_db = command.volumeDb
		"VoiceSetPitch":
			if player != null: player.pitch_scale = command.pitch
		"VoiceSetPaused":
			if player != null: player.stream_paused = command.paused
		"VoiceSet3D":
			if player is AudioStreamPlayer3D: player.global_transform = to_transform(command.attributes)
		"BusSetVolume":
			if index >= 0:
				if _faders.has(bus):
					_faders[bus].effect.volume_db = command.volumeDb
					if _faders[bus].bypassed: AudioServer.set_bus_volume_db(index, command.volumeDb)
				else: AudioServer.set_bus_volume_db(index, command.volumeDb)
		"BusSetMute":
			if index >= 0: AudioServer.set_bus_mute(index, command.mute)
		"BusSetSolo":
			if index >= 0: AudioServer.set_bus_solo(index, command.solo)
		"BusSetBypass":
			if index >= 0:
				AudioServer.set_bus_bypass_effects(index, command.bypass)
				if _faders.has(bus):
					_faders[bus].bypassed = command.bypass
					AudioServer.set_bus_volume_db(index, _faders[bus].effect.volume_db if command.bypass else 0.0)
		"EffectSetEnabled":
			if index >= 0 and command.effectIndex >= 0 and command.effectIndex < effects_of(bus).size():
				AudioServer.set_bus_effect_enabled(index, engine_effect_index(bus, command.effectIndex), command.enabled)
		"EffectSetParameter": set_effect_parameter(bus, command.effectIndex, command.parameter, command.value)
		_: push_error("Dirge: unhandled command '%s'." % command.get("type", ""))

func _play(command: Dictionary) -> void:
	var voice = int(command.voice)
	var spatial: bool = command.get("spatial", false)
	var slot: Dictionary = _voices[voice].slot if _voices.has(voice) else {}
	if not slot.is_empty() and (slot.spatial != spatial or slot.sync != null):
		_release(voice)
		slot = {}
	if slot.is_empty():
		slot = _acquire(spatial, false)
		slot.members = [voice]
		_voices[voice] = {"slot": slot, "index": -1}
	var stream = assets.get_looping_stream(command.asset) if command.get("loop", false) else assets.get_stream(command.asset)
	if stream == null:
		_release(voice)
		_finished_feedback(voice)
		return
	_start_player(slot, command, stream, command.get("volumeDb", 0.0))

func _play_group(group: Array) -> void:
	for command in group:
		_release(command.voice)
	var lead: Dictionary = group[0]
	var slot = _acquire(lead.get("spatial", false), true)
	slot.sync.stream_count = group.size()
	slot.members = []
	for k in range(group.size()):
		var command: Dictionary = group[k]
		var stream = assets.get_looping_stream(command.asset) if command.get("loop", false) else assets.get_stream(command.asset)
		slot.sync.set_sync_stream(k, stream)
		slot.sync.set_sync_stream_volume(k, command.get("volumeDb", 0.0) if stream != null else -100.0)
		slot.members.append(command.voice if stream != null else -1)
		if stream == null:
			_finished_feedback(command.voice)
		else:
			_voices[command.voice] = {"slot": slot, "index": k}
	var count = _live_members(slot)
	if count == 0:
		_release_slot(slot)
		return
	_start_player(slot, lead, slot.sync, 0.0)
	if verbose:
		print("Dirge: sync group %s: %s layers on '%s'." % [lead.syncGroup, count, slot.player.name])
	sync_group_started.emit(lead.syncGroup, count)

func _start_player(slot: Dictionary, command: Dictionary, stream: AudioStream, volume: float) -> void:
	var player = slot.player
	player.stream = stream
	player.bus = _names.get(command.get("bus", "Master"), &"Master")
	player.volume_db = volume
	player.pitch_scale = command.get("pitch", 1.0)
	player.stream_paused = false
	if slot.spatial:
		apply_spatial_macros(player, command)
		player.global_transform = to_transform(command.get("attributes", {}))
	player.play(command.get("startSeconds", 0.0))

func _acquire(spatial: bool, synchronized: bool) -> Dictionary:
	var pool = int(spatial) + 2 * int(synchronized)
	var slot: Dictionary
	if not _free[pool].is_empty():
		slot = _free[pool].pop_back()
	else:
		var player: Node = AudioStreamPlayer3D.new() if spatial else AudioStreamPlayer.new()
		player.name = "%s%s" % ["Sync" if synchronized else "Voice", _all.size()]
		slot = {"player": player, "spatial": spatial, "sync": AudioStreamSynchronized.new() if synchronized else null, "members": [], "pool": pool, "active": false}
		player.finished.connect(_on_finished.bind(slot))
		add_child(player)
		_all.append(slot)
	slot.active = true
	return slot

func _live_members(slot: Dictionary) -> int:
	var count = 0
	for voice in slot.members:
		if voice >= 0: count += 1
	return count

func _release(voice: int) -> void:
	if not _voices.has(voice):
		return
	var binding: Dictionary = _voices[voice]
	var slot: Dictionary = binding.slot
	if slot.sync == null:
		_release_slot(slot)
		return
	_voices.erase(voice)
	slot.members[binding.index] = -1
	slot.sync.set_sync_stream_volume(binding.index, -100.0)
	if _live_members(slot) == 0:
		_release_slot(slot)

func _release_slot(slot: Dictionary) -> void:
	if not slot.active:
		return
	for voice in slot.members:
		if voice >= 0: _voices.erase(voice)
	slot.active = false
	slot.player.stop()
	slot.player.stream_paused = false
	slot.player.stream = null
	if slot.sync != null:
		for k in range(slot.members.size()):
			slot.sync.set_sync_stream(k, null)
	slot.members = []
	_free[slot.pool].append(slot)

func _on_finished(slot: Dictionary) -> void:
	if not slot.active:
		return
	var members: Array = slot.members.duplicate()
	_release_slot(slot)
	for voice in members:
		if voice >= 0: _finished_feedback(voice)

func _finished_feedback(voice: int) -> void:
	voice_finished.emit(voice)
	feedback.emit({"type": "VoiceFinished", "voice": voice})

func stop_all() -> void:
	for slot in _all:
		_release_slot(slot)

func shutdown() -> void:
	stop_all()
	capture = null
	for slot in _all:
		slot.player.queue_free()
	_all.clear()
	_voices.clear()
	for pool in _free:
		pool.clear()

static func apply_spatial_macros(player: AudioStreamPlayer3D, command: Dictionary) -> void:
	var minimum = float(command.get("minDistance", 1.0))
	player.unit_size = minimum if minimum > 0.0 else 1.0
	player.max_distance = command.get("maxDistance", 20.0)
	player.attenuation_model = {"Inverse": 0, "InverseSquare": 1, "Logarithmic": 2, "Disabled": 3}.get(command.get("rolloff", "Inverse"), 0)
	player.doppler_tracking = AudioStreamPlayer3D.DOPPLER_TRACKING_PHYSICS_STEP if command.get("doppler", false) and command.get("dopplerScale", 1.0) > 0 else AudioStreamPlayer3D.DOPPLER_TRACKING_DISABLED

static func to_transform(attributes: Dictionary) -> Transform3D:
	var forward: Vector3 = attributes.get("forward", Vector3.FORWARD)
	var up: Vector3 = attributes.get("up", Vector3.UP)
	var basis = Basis.IDENTITY if forward.is_zero_approx() or up.is_zero_approx() or forward.cross(up).is_zero_approx() else Basis.looking_at(forward, up)
	return Transform3D(basis, attributes.get("position", Vector3.ZERO))
