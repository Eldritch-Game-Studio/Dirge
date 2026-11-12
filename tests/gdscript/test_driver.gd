extends RefCounted

const Driver = preload("res://addons/dirge/runtime/audio_driver.gd")
const Catalog = preload("res://addons/dirge/runtime/asset_catalog.gd")
const Banks = preload("res://addons/dirge/runtime/banks.gd")
const Capture = preload("res://addons/dirge/runtime/loudness_capture.gd")
const Server = preload("res://addons/dirge/runtime/dirge_server.gd")
const Client = preload("res://addons/dirge/runtime/live_update_client.gd")
const Protocol = preload("res://addons/dirge/runtime/live_protocol.gd")
const Json = preload("res://addons/dirge/model/Serialization/project_json.gd")
const TONE = "memory://dirge-driver-tone"
const SHORT = "memory://dirge-driver-short"
const IMPULSE = "memory://dirge-driver-impulse"
var _tree: SceneTree

static func _id(value: int) -> String:
	return "%032x" % value

static func _tone(seconds: float = 1.0) -> AudioStreamWAV:
	var stream = AudioStreamWAV.new()
	stream.format = AudioStreamWAV.FORMAT_16_BITS
	stream.mix_rate = roundi(AudioServer.get_mix_rate())
	var frames = roundi(seconds * stream.mix_rate)
	var data = PackedByteArray()
	data.resize(frames * 2)
	for index in range(frames):
		data.encode_s16(index * 2, roundi(8192.0 * sin(TAU * 1000.0 * index / stream.mix_rate)))
	stream.data = data
	return stream

static func _impulse() -> AudioStreamWAV:
	var stream = AudioStreamWAV.new()
	stream.format = AudioStreamWAV.FORMAT_16_BITS
	stream.mix_rate = roundi(AudioServer.get_mix_rate())
	var data = PackedByteArray()
	data.resize(2048)
	data.encode_s16(0, 32767)
	stream.data = data
	return stream

static func _bus(value: int, bus_name: String, parent: int = 1, effects: Array = []) -> Dictionary:
	return {"id": _id(value), "name": bus_name, "parent": _id(parent), "volumeDb": 0.0, "effects": effects, "vcas": []}

static func _effect(type_name: String, parameters: Dictionary = {}, strings: Dictionary = {}) -> Dictionary:
	return {"id": _id(100 + parameters.size()), "typeName": type_name, "enabled": true, "parameters": parameters, "stringParameters": strings}

static func _project() -> Dictionary:
	return {"name": "Driver test", "events": [], "globalParameters": [], "snapshots": [], "banks": [],
		"mixer": {"buses": [_bus(1, "Master", 0), _bus(2, "DriverMusic")], "vcas": []}}

static func _play(voice: int, asset: String = TONE, group: int = 0) -> Dictionary:
	return {"type": "VoicePlay", "voice": voice, "asset": asset, "bus": "DriverMusic", "startSeconds": 0.0,
		"loop": true, "volumeDb": -6.0, "pitch": 1.0, "spatial": false, "attributes": {},
		"minDistance": 1.0, "maxDistance": 20.0, "syncGroup": group}

func run(test: Object) -> void:
	_tree = Engine.get_main_loop() as SceneTree
	var layout = AudioServer.generate_bus_layout()
	var assets = Catalog.new()
	var tone = _tone()
	assets.register(TONE, tone)
	assets.register(SHORT, _tone(0.05))
	assets.register(IMPULSE, _impulse())
	test.check(is_equal_approx(assets.get_length(TONE), 1.0), "Catalog reports actual engine stream duration")
	var looping = assets.get_looping_stream(TONE)
	test.check(looping != tone and tone.loop_mode == AudioStreamWAV.LOOP_DISABLED and looping.loop_mode == AudioStreamWAV.LOOP_FORWARD, "Looping duplicates do not mutate imported resources")
	test.check(looping == assets.get_looping_stream(TONE) and looping.loop_end == tone.mix_rate, "Loop duplicates cache and cover whole asset")
	var driver = Driver.new(assets)
	_tree.root.add_child(driver)
	await _bus_and_voices(test, driver)
	await _native_audio(test, driver)
	await _banks(test, assets)
	_protocol(test)
	driver.shutdown()
	driver.queue_free()
	await _tree.process_frame
	await _server_and_live(test, assets)
	AudioServer.set_bus_layout(layout)
	for node in _tree.root.get_children():
		if str(node.name).begins_with("DirgeReturnKeepAlive_Driver"):
			node.queue_free()
	await _tree.process_frame

func _bus_and_voices(test: Object, driver) -> void:
	var project = _project()
	project.mixer.buses[1].effects = [_effect("AudioEffectAmplify", {"volume_db": -3.0}), _effect("AudioEffectLowPassFilter", {"cutoff_hz": 3000.0})]
	project.mixer.buses[1].faderIndex = 1
	project.mixer.buses[1].volumeDb = -8.0
	test.check(driver.load(project), "Engine bus graph loads")
	var index = driver.bus_index("DriverMusic")
	test.check(index == 1 and AudioServer.get_bus_send(index) == &"Master", "Bus hierarchy creates parent-first sends")
	test.check(driver.effects_of("DriverMusic").size() == 2 and AudioServer.get_bus_effect_count(index) == 3, "Fader inserts without changing authored effect indices")
	test.check(driver.engine_effect_index("DriverMusic", 0) == 0 and driver.engine_effect_index("DriverMusic", 1) == 2, "Authored indices map around in-chain fader")
	test.check(driver.fader_of("DriverMusic").volume_db == -8.0 and AudioServer.get_bus_volume_db(index) == 0.0, "In-chain fader owns volume while real bus stays unity")
	driver.apply([{"type": "BusSetVolume", "bus": "DriverMusic", "volumeDb": -12.0}, {"type": "BusSetBypass", "bus": "DriverMusic", "bypass": true}])
	test.check(AudioServer.get_bus_volume_db(index) == -12.0 and AudioServer.is_bus_bypassing_effects(index), "Bypass mirrors fader onto real bus")
	driver.apply({"type": "BusSetVolume", "bus": "DriverMusic", "volumeDb": -4.0})
	test.check(AudioServer.get_bus_volume_db(index) == -4.0, "Bypassed fader continues tracking volume")
	driver.apply([{"type": "BusSetBypass", "bus": "DriverMusic", "bypass": false}, {"type": "EffectSetEnabled", "bus": "DriverMusic", "effectIndex": 1, "enabled": false}, {"type": "EffectSetParameter", "bus": "DriverMusic", "effectIndex": 1, "parameter": "cutoff_hz", "value": 900.0}])
	test.check(AudioServer.get_bus_volume_db(index) == 0.0 and not AudioServer.is_bus_effect_enabled(index, 2), "Unbypass restores unity and enabled command uses shifted slot")
	test.check(driver.effects_of("DriverMusic")[1].cutoff_hz == 900.0, "Effect parameters address authored slot")
	driver.apply([{"type": "BusSetMute", "bus": "DriverMusic", "mute": true}, {"type": "BusSetSolo", "bus": "DriverMusic", "solo": true}])
	test.check(AudioServer.is_bus_mute(index) and AudioServer.is_bus_solo(index), "Mute and solo reach engine")
	driver.apply([{"type": "BusSetMute", "bus": "DriverMusic", "mute": false}, {"type": "BusSetSolo", "bus": "DriverMusic", "solo": false}])
	var feedback: Array = []
	driver.feedback.connect(func(message): feedback.append(message))
	driver.apply(_play(1))
	var player = driver.player_of(1)
	test.check(player is AudioStreamPlayer and driver.is_voice_playing(1), "2D command starts real engine player")
	driver.apply([{"type": "VoiceSetVolume", "voice": 1, "volumeDb": -9.0}, {"type": "VoiceSetPitch", "voice": 1, "pitch": 1.25}, {"type": "VoiceSetPaused", "voice": 1, "paused": true}])
	test.check(player.volume_db == -9.0 and player.pitch_scale == 1.25 and player.stream_paused, "Voice controls update real playback")
	driver.apply({"type": "VoiceSetPaused", "voice": 1, "paused": false})
	driver.apply({"type": "VoiceSeek", "voice": 1, "seconds": 0.3})
	await _tree.create_timer(0.04).timeout
	test.check(player.get_playback_position() >= 0.29, "Voice seek moves native playback cursor")
	driver.apply({"type": "VoiceStop", "voice": 1})
	test.check(driver.player_of(1) == null and not player.playing and feedback.is_empty(), "Explicit stop releases player without finished feedback")
	driver.apply(_play(2))
	test.check(driver.player_of(2) == player, "Released 2D player is pooled")
	var spatial = _play(2)
	spatial.spatial = true
	spatial.attributes = {"position": Vector3(2, 3, 4), "forward": Vector3.RIGHT, "up": Vector3.UP}
	spatial.minDistance = 0.0
	spatial.maxDistance = 70.0
	spatial.rolloff = "InverseSquare"
	spatial.doppler = true
	spatial.dopplerScale = 0.5
	driver.apply(spatial)
	var player3d = driver.player_of(2)
	test.check(player3d is AudioStreamPlayer3D and player3d.global_position == Vector3(2, 3, 4), "Changing spatial kind replaces player and applies transform")
	test.check(player3d.unit_size == 1.0 and player3d.max_distance == 70.0 and player3d.attenuation_model == AudioStreamPlayer3D.ATTENUATION_INVERSE_SQUARE_DISTANCE and player3d.doppler_tracking == AudioStreamPlayer3D.DOPPLER_TRACKING_PHYSICS_STEP, "Spatial distance, rolloff and doppler map to engine macros")
	driver.apply({"type": "VoiceSet3D", "voice": 2, "attributes": {"position": Vector3.ONE, "forward": Vector3.ZERO}})
	test.check(player3d.global_transform == Transform3D(Basis.IDENTITY, Vector3.ONE), "Degenerate spatial orientation uses identity")
	driver.stop_all()
	driver.apply([_play(3, TONE, 4), _play(4, TONE, 4)])
	var sync_player = driver.player_of(3)
	test.check(driver.is_synchronized(3) and driver.player_of(4) == sync_player and sync_player.stream is AudioStreamSynchronized, "Same-batch sync group shares actual synchronized playback")
	driver.apply({"type": "VoiceSetVolume", "voice": 4, "volumeDb": -15.0})
	test.check(sync_player.stream.get_sync_stream_volume(1) == -15.0 and sync_player.volume_db == 0.0, "Layer volume updates only its substream")
	driver.apply({"type": "VoiceStop", "voice": 3})
	test.check(driver.player_of(3) == null and driver.is_voice_playing(4) and sync_player.stream.get_sync_stream_volume(0) == -100.0, "Stopping sync leader silences only that layer")
	driver.apply({"type": "VoiceStop", "voice": 4})
	test.check(not sync_player.playing, "Last synchronized member releases group")
	var a = _play(5, SHORT, 7)
	var b = _play(6, SHORT, 7)
	a.loop = false
	b.loop = false
	driver.apply([a, b])
	await _tree.create_timer(0.3).timeout
	test.check(feedback.has({"type": "VoiceFinished", "voice": 5}) and feedback.has({"type": "VoiceFinished", "voice": 6}), "Natural synchronized completion feeds back every live member")
	driver.assets.register("memory://missing", null)
	driver.apply(_play(9, "memory://missing"))
	test.check(feedback.has({"type": "VoiceFinished", "voice": 9}) and driver.player_of(9) == null, "Known-missing stream immediately retires model voice")
	var group: Array = []
	for voice in range(100, 133): group.append(_play(voice, TONE, 20))
	driver.apply(group)
	test.check(driver.player_of(100) == driver.player_of(131) and driver.player_of(132) != driver.player_of(100), "Sync group exceeds engine capacity without dropping extra voices")
	driver.stop_all()

func _native_audio(test: Object, driver) -> void:
	for type_name in ["DirgeAudioEffectSend", "DirgeAudioEffectReturn", "DirgeAudioEffectConvolutionReverb"]:
		test.check(ClassDB.class_exists(type_name) and ClassDB.can_instantiate(type_name), "Required native class available: " + type_name)
		if not ClassDB.class_exists(type_name) or not ClassDB.can_instantiate(type_name):
			return
	var project = _project()
	project.mixer.buses = [_bus(1, "Master", 0), _bus(3, "DriverSender", 1, [_effect("DirgeAudioEffectSend", {"level_db": 0.0, "pre_fader": 1.0}, {"target_bus": "DriverReturn"})]), _bus(4, "DriverReturn", 1, [_effect("DirgeAudioEffectReturn")])]
	project.mixer.buses[1].volumeDb = -60.0
	test.check(driver.load(project), "Native send/return layout loads without fallback")
	var receiver = driver.bus_index("DriverReturn")
	test.check(receiver < driver.bus_index("DriverSender"), "Return ordered lower than sender for same-block delivery")
	var capture = AudioEffectCapture.new()
	capture.buffer_length = 1.0
	AudioServer.add_bus_effect(receiver, capture)
	var play = _play(200)
	play.bus = "DriverSender"
	play.volumeDb = 0.0
	driver.apply(play)
	await _tree.create_timer(0.2).timeout
	var pre = _rms(capture)
	test.check(pre > 0.02, "Native pre-fader send routes nonzero PCM despite attenuated source bus")
	driver.set_effect_parameter("DriverSender", 0, "pre_fader", false)
	await _tree.create_timer(0.1).timeout
	capture.clear_buffer()
	await _tree.create_timer(0.15).timeout
	var post = _rms(capture)
	test.check(post < pre * 0.02, "Native post-fader send includes source bus attenuation")
	driver.set_effect_parameter("DriverSender", 0, "pre_fader", true)
	driver.set_effect_parameter("DriverSender", 0, "level_db", -12.0)
	await _tree.create_timer(0.1).timeout
	capture.clear_buffer()
	await _tree.create_timer(0.15).timeout
	var attenuated = _rms(capture)
	test.check(attenuated > pre * 0.15 and attenuated < pre * 0.4, "Native send level changes measured PCM gain")
	driver.stop_all()
	project = _project()
	project.mixer.buses[1].effects = [_effect("DirgeAudioEffectConvolutionReverb", {"dry_db": -80.0, "wet_db": 0.0, "gain_db": 0.0}, {"impulse": IMPULSE})]
	test.check(driver.load(project), "Native convolution effect loads impulse AudioStream from authored path")
	var convolver = driver.effects_of("DriverMusic")[0]
	test.check(convolver.get("impulse") == driver.assets.get_stream(IMPULSE), "Convolver receives actual AudioStream resource")
	var convolution_capture = AudioEffectCapture.new()
	convolution_capture.buffer_length = 1.0
	AudioServer.add_bus_effect(driver.bus_index("DriverMusic"), convolution_capture)
	var loudness = Capture.new()
	driver.capture = loudness
	driver.apply(_play(201))
	for tick in range(12):
		await _tree.create_timer(0.1).timeout
		loudness.poll()
	var wet = _rms(convolution_capture)
	test.check(wet > 0.01, "Native wet-only convolution produces real audio through impulse kernel")
	test.check(is_finite(loudness.momentary_lufs) and is_finite(loudness.integrated_lufs) and is_finite(loudness.true_peak_dbtp), "Master PCM capture feeds BS1770 loudness and true peak")
	driver.set_effect_parameter("DriverMusic", 0, "wet_db", -80.0)
	await _tree.create_timer(0.1).timeout
	convolution_capture.clear_buffer()
	await _tree.create_timer(0.15).timeout
	test.check(_rms(convolution_capture) < wet * 0.001, "Native wet and dry silence parameters mute actual convolution output")
	loudness.reset()
	test.check(loudness.momentary_lufs == -INF and loudness.integrated_lufs == -INF, "Loudness reset flushes gating history")
	driver.stop_all()
	driver.load(_project())
	test.check(loudness.is_installed and AudioServer.get_bus_effect(0, AudioServer.get_bus_effect_count(0) - 1) == loudness.effect, "Loudness capture survives mixer rebuild appended after authored chain")
	driver.capture = null
	test.check(not loudness.is_installed, "Removing capture uninstalls engine effect")

static func _rms(capture: AudioEffectCapture) -> float:
	var frames = capture.get_buffer(capture.get_frames_available())
	var energy = 0.0
	for frame in frames:
		energy += frame.length_squared()
	return sqrt(energy / (2.0 * frames.size())) if not frames.is_empty() else 0.0

func _banks(test: Object, assets) -> void:
	var path = "user://dirge_driver_bank_test.tres"
	test.check(ResourceSaver.save(_tone(0.1), path) == OK, "Bank test resource saved as real AudioStream")
	var project = _project()
	project.events = [{"id": _id(30), "tracks": [{"instruments": [{"$type": "Event", "event": _id(31)}]}]}, {"id": _id(31), "tracks": [{"instruments": [{"$type": "Clip", "asset": path}, {"$type": "Multi", "entries": [{"asset": path}]}, {"$type": "Scatterer", "entries": [{"asset": path}]}, {"$type": "Event", "event": _id(30)}]}]}]
	project.banks = [{"name": "A", "events": [_id(30)]}, {"name": "B", "events": [_id(31)]}]
	var banks = Banks.new(assets)
	test.check(banks.load_bank(project, "A") and banks.load_bank(project, "B"), "Banks preload nested, multi and scatterer assets with cycle guard")
	for tick in range(120):
		banks.poll()
		if banks.is_loaded("A") and banks.is_loaded("B"):
			break
		await _tree.process_frame
	test.check(banks.is_loaded("A") and banks.is_loaded("B") and assets.get_stream(path) is AudioStream, "Threaded engine resource loads complete shared banks")
	test.check(banks.banks.A.paths.size() == 1, "Bank paths are deduplicated across instrument forms")
	banks.unload("A")
	test.check(assets.is_cached(path), "Shared bank retains catalog reference")
	banks.unload("B")
	test.check(not assets.is_cached(path) and banks.pending_count("B") == -1, "Last bank evicts catalog caches")
	banks.load_bank(project, "A")
	banks.unload("A")
	for tick in range(120):
		banks.poll()
		if banks._inflight.is_empty(): break
		await _tree.process_frame
	test.check(not assets.is_cached(path), "Late threaded completion after unload does not resurrect cache")
	DirAccess.remove_absolute(path)

func _protocol(test: Object) -> void:
	var message = {"type": "stats", "time": 2.0, "momentaryLufs": -INF, "shortTermLufs": NAN, "integratedLufs": INF, "buses": [{"name": "音楽", "left": -INF, "right": -3.0}]}
	var bytes = Protocol.encode(message)
	var reader = Protocol.FrameReader.new()
	var cut = int(bytes.size() / 2)
	test.check(reader.append(bytes.slice(0, cut)) and reader.read_frame() == null, "Partial frame remains buffered")
	reader.append(bytes.slice(cut))
	var decoded = Protocol.decode(reader.read_frame())
	test.check(decoded.protocol == 1 and decoded.momentaryLufs == -INF and is_nan(decoded.shortTermLufs) and decoded.integratedLufs == INF and decoded.buses[0].name == "音楽", "UTF8 framing and named nonfinite float protocol are compatible")
	test.check(Protocol.decode('{"type":"stats","protocol":2}').is_empty() and Protocol.decode('{"type":"other"}').is_empty(), "Unknown protocol and discriminator reject")
	test.check(Protocol.decode('{"type":"set-param","value":{}}').is_empty() and Protocol.decode('{"type":"stop","immediate":"yes"}').is_empty(), "Malformed typed payloads reject before dispatch")
	test.check(Protocol.decode('{"type":"hello","Version":"1.1.0","PROJECTNAME":"Infinity"}').projectName == "Infinity", "Case-insensitive fields preserve string values")
	reader.append(Protocol.encode({"type": "start", "path": "A"}) + Protocol.encode({"type": "stop", "handle": 1}))
	test.check(Protocol.decode(reader.read_frame()).type == "start" and Protocol.decode(reader.read_frame()).type == "stop" and reader.read_frame() == null, "Multiple TCP frames split independently")

static func _server_project() -> Dictionary:
	var project = _project()
	project.globalParameters = [{"id": _id(50), "name": "Global", "minimum": 0.0, "maximum": 1.0, "default": 0.0}]
	project.events = [{"id": _id(51), "path": "Driver/Loop", "bus": _id(2), "macros": {"persistent": true}, "localParameters": [{"id": _id(52), "name": "Local", "minimum": 0.0, "maximum": 1.0, "default": 0.0}], "tracks": [{"id": _id(53), "instruments": [{"$type": "Clip", "id": _id(54), "asset": TONE, "loop": true, "placement": {"start": 0.0, "length": 5.0}}]}]}, {"id": _id(55), "path": "Driver/Programmer", "bus": _id(2), "tracks": [{"id": _id(56), "instruments": [{"$type": "Programmer", "id": _id(57), "key": "voice", "placement": {"start": 0.0, "length": 1.0}}]}]}]
	project.snapshots = [{"id": _id(58), "name": "Quiet", "values": [{"target": {"kind": "BusVolumeDb", "owner": _id(2)}, "value": -10.0}]}]
	return Json.deserialize(JSON.stringify(project))

func _server_and_live(test: Object, assets) -> void:
	var server = Server.new()
	server.MeterLoudness = false
	_tree.root.add_child(server)
	server.set_process(false)
	server.Assets.register(TONE, assets.get_stream(TONE))
	var project = _server_project()
	server.Load(project)
	test.check(server.IsLoaded and server.Version == "1.1.0", "PascalCase server loads GDScript runtime")
	var callbacks: Array = []
	server.EventStarted.connect(func(handle): callbacks.append(["started", handle]))
	server.EventStopped.connect(func(handle): callbacks.append(["stopped", handle]))
	server.ProgrammerSound.connect(func(handle, _key): server.SetProgrammerSound(handle, TONE))
	var handle = server.StartEvent("Driver/Loop")
	server.SetParameter(handle, "Local", 0.7)
	server.SetGlobalParameter("Global", 0.4)
	server.StartSnapshot("Quiet")
	server.System.update(0.0)
	server.flush()
	test.check(handle > 0 and server.IsEventPlaying(handle) and callbacks.has(["started", handle]), "Handle lifecycle forwards started signal")
	test.check(is_equal_approx(server.GetParameter(handle, "Local"), 0.7) and is_equal_approx(server.GetGlobalParameter("Global"), 0.4), "PascalCase parameter APIs reach local/global banks")
	var programmer = server.StartEvent("Driver/Programmer")
	server.System.update(0.0)
	server.flush()
	test.check(server.GetInstance(programmer) != null and server.System.real_voice_count >= 2, "Synchronous programmer signal supplies real asset")
	server.GetInstance(handle).timeline_position = 0.4
	server.System.update(0.0)
	server.flush()
	server.HotReload(project.duplicate(true))
	server.System.update(0.0)
	server.flush()
	test.check(server.IsEventPlaying(handle) and is_equal_approx(server.GetGlobalParameter("Global"), 0.4) and server.IsSnapshotActive("Quiet"), "Hot reload retains handle numbers, globals and active snapshots")
	test.check(is_equal_approx(server.GetInstance(handle).timeline_position, 0.4), "Hot reload restores in-range timeline position")
	server.SetCallbackMask(handle, 0)
	var callback_count = callbacks.size()
	server.StopEvent(handle, true)
	server.System.update(0.0)
	server.flush()
	server._sweep()
	test.check(not server.IsEventPlaying(handle) and server.GetInstance(handle) == null and callbacks.size() == callback_count, "Callback masks clip signals and stopped handles sweep")
	server.StopSnapshot("Quiet")
	test.check(not server.IsSnapshotActive("Quiet"), "Snapshot stop releases all tracked instances")
	server.EnableLiveUpdate(17447)
	test.check(server.Live.is_listening, "Live host listens on loopback")
	var client = Client.new()
	_tree.root.add_child(client)
	var started: Array = []
	var errors: Array = []
	client.started.connect(func(value, path): started.append([value, path]))
	client.error_received.connect(func(message): errors.append(message))
	test.check(client.connect_to_game("127.0.0.1", 17447) == OK, "Live client begins real TCP connection")
	for tick in range(120):
		if client.is_connected_to_game and not client.last_stats.is_empty(): break
		await _tree.process_frame
	test.check(client.is_connected_to_game and client.hello_info.get("version") == "1.1.0" and client.hello_info.get("projectName") == project.name, "Live hello negotiates protocol over actual socket")
	test.check(client.last_stats.has("buses") and client.last_stats.get("momentaryLufs") == -INF, "Live stats carry bus peaks and named silence values")
	client.send_start("Driver/Loop")
	for tick in range(120):
		if not started.is_empty(): break
		await _tree.process_frame
	test.check(not started.is_empty() and started[0][0] > 0, "Live start returns server handle")
	if not started.is_empty():
		var live_handle = started[0][0]
		client.send_parameter("Global", 0.8)
		client.send_bus_volume("DriverMusic", -7.0)
		client.send_snapshot("Quiet")
		client.send_stop(live_handle, true)
		client.send({"type": "mixer-set", "bus": "missing", "volumeDb": 1.0})
		for tick in range(120):
			if not errors.is_empty(): break
			await _tree.process_frame
		server.System.update(0.0)
		server.flush()
		test.check(is_equal_approx(server.GetGlobalParameter("Global"), 0.8) and server.IsSnapshotActive("Quiet") and not server.IsEventPlaying(live_handle), "Live commands dispatch parameters, snapshots and stop")
		test.check(not errors.is_empty() and client.is_connected_to_game, "Rejected live request reports error without disconnect")
	var replacement = Client.new()
	_tree.root.add_child(replacement)
	replacement.connect_to_game("127.0.0.1", 17447)
	for tick in range(120):
		if replacement.is_connected_to_game and not client.is_connected_to_game: break
		await _tree.process_frame
	test.check(replacement.is_connected_to_game and not client.is_connected_to_game, "Second live editor replaces first connection")
	replacement.send_project(project)
	await _tree.create_timer(0.05).timeout
	test.check(server.IsLoaded and server.System.project.name == project.name, "Live full-project replace hot reloads valid project")
	replacement.disconnect_from_game()
	client.queue_free()
	replacement.queue_free()
	server.DisableLiveUpdate()
	server.Unload()
	test.check(not server.IsLoaded and server.Handles.is_empty(), "Unload drops model, handles and voices")
	server.queue_free()
	await _tree.process_frame
