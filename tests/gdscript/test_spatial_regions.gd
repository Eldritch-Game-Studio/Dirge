extends RefCounted
## Explicit spatial regions: region-local listener selection and stealing, recording identity, and real-engine
## driver routing/lifecycle in distinct World3Ds under one mixer. Not audible-output proof.

const System = preload("res://addons/dirge/model/Runtime/dirge_system.gd")
const Recorder = preload("res://addons/dirge/model/Profiling/recorder.gd")
const Replayer = preload("res://addons/dirge/model/Profiling/replayer.gd")
const RecordingJson = preload("res://addons/dirge/model/Profiling/recording_json.gd")
const CommandText = preload("res://addons/dirge/model/Profiling/command_text.gd")
const Driver = preload("res://addons/dirge/runtime/audio_driver.gd")
const Catalog = preload("res://addons/dirge/runtime/asset_catalog.gd")
const TONE = "memory://dirge-region-tone"
const SHORT = "memory://dirge-region-short"
const MISSING = "memory://dirge-region-missing"
var _serial: int = 1
var _tree: SceneTree

func _id() -> String:
	var result = "%032x" % _serial
	_serial += 1
	return result

static func _at(x: float, region: int = 0) -> Dictionary:
	return {"position": Vector3(x, 0, 0), "region": region}

func _project(scatter: bool = false) -> Dictionary:
	var instrument: Dictionary = {"id": _id(), "placement": {"start": 0.0, "length": 0.0}}
	if scatter:
		instrument.merge({"$type": "Scatterer", "minInterval": 0.1, "maxInterval": 0.1, "minScatterDistance": 2, "maxScatterDistance": 2, "entries": [{"asset": "res://tone.wav"}]})
	else:
		instrument.merge({"$type": "Clip", "asset": "res://tone.wav", "loop": true})
	var event = {"id": _id(), "path": "Spatial", "macros": {"is3D": true, "persistent": true}, "markers": [], "tempo": [], "automation": [], "modulators": [],
		"localParameters": [
			{"id": _id(), "name": "Distance", "minimum": 0.0, "maximum": 1000.0, "default": 0.0, "builtIn": "Distance"},
			{"id": _id(), "name": "Normalized", "minimum": 0.0, "maximum": 1.0, "default": 0.0, "builtIn": "DistanceNormalized"}],
		"tracks": [{"id": _id(), "volumeDb": 0.0, "instruments": [instrument]}]}
	return {"events": [event], "globalParameters": [], "assetLengths": {"res://tone.wav": 10.0}, "snapshots": [], "audioTables": [],
		"mixer": {"buses": [{"id": _id(), "name": "Master"}], "vcas": []}}

static func _start(system, attributes: Dictionary):
	var instance = system.get_event("Spatial").create_instance()
	instance.set_3d_attributes(attributes)
	instance.start()
	return instance

static func _of(system, type: String) -> Array:
	return system.commands.filter(func(command): return command.type == type)

func run(test: Object) -> void:
	_listener_selection(test)
	_stealing_and_mutation(test)
	_recordings(test)
	await _driver_lifecycle(test)

func _listener_selection(test: Object) -> void:
	var system = System.new(_project())
	system.set_listener(0, _at(0, 1))
	system.set_listener(1, _at(0, 2))
	var a = _start(system, _at(0, 1))
	var b = _start(system, _at(0, 2))
	system.update(0.01)
	test.check(a.get_parameter("Distance") == 0 and b.get_parameter("Distance") == 0, "same coordinates resolve each region's own listener")
	system.set_listener(0, _at(25, 1))
	system.update(0.01)
	test.check(a.get_parameter("Distance") == 25 and b.get_parameter("Distance") == 0, "same coordinates never borrow another region's listener")
	var regions: Array = _of(system, "VoicePlay").map(func(play): return play.attributes.region)
	regions.sort()
	test.check(regions == [1, 2], "VoicePlay carries each emitter's region")

	system = System.new(_project())
	var lonely = _start(system, _at(0, 7))
	system.update(0.01)
	test.check(lonely.get_parameter("Distance") == 1000 and lonely.get_parameter("Normalized") == 1, "missing region listener is maximally distant, not the default origin")
	system.set_listener(1, _at(10, 7))
	system.update(0.01)
	test.check(lonely.get_parameter("Distance") == 10, "enabled region listener drives built-ins")
	system.set_listener(1, _at(10, 7), 0)
	system.update(0.01)
	test.check(lonely.get_parameter("Distance") == 1000, "disabled region listener is not used")

	system = System.new(_project())
	system.set_listener(0, _at(10, 1))
	system.set_listener(1, _at(4, 1))
	system.set_listener(2, _at(0.5, 2))
	var nearest = _start(system, _at(0, 1))
	system.update(0.01)
	test.check(nearest.get_parameter("Distance") == 4, "nearest enabled listener is selected within the emitter's region")
	system.set_listener(1, _at(4, 1), 0)
	system.update(0.01)
	test.check(nearest.get_parameter("Distance") == 10, "next-nearest region listener takes over when one is disabled")
	system.set_listener(0, _at(10, 1), 0)
	system.update(0.01)
	test.check(nearest.get_parameter("Distance") == 1000, "a nearer listener in another region is never borrowed")

	# Hosts that never use regions (the single-world default) keep legacy selection exactly.
	system = System.new(_project())
	system.set_listener(0, _at(5))
	system.set_listener(1, _at(-2))
	var legacy = _start(system, {"position": Vector3.ZERO})
	system.update(0.01)
	test.check(legacy.get_parameter("Distance") == 2, "default region selects the nearest enabled listener")
	system.set_listener(1, _at(-2), 0)
	system.update(0.01)
	test.check(legacy.get_parameter("Distance") == 5, "default region ignores disabled listeners")
	system.set_listener(0, _at(5), 0)
	system.update(0.01)
	test.check(legacy.get_parameter("Distance") == 5, "all-disabled default listeners fall back to listener 0")
	test.check(_of(system, "VoicePlay").size() == 1 and _of(system, "VoicePlay")[0].attributes.region == 0, "omitted region is the legacy world")

func _stealing_and_mutation(test: Object) -> void:
	var project = _project()
	project.events[0].macros.merge({"maxInstances": 2, "stealing": "Furthest"})
	var system = System.new(project)
	system.set_listener(0, _at(0, 1))
	system.set_listener(1, _at(50, 2))
	var near = _start(system, _at(1, 1))
	var far = _start(system, _at(0, 2))
	system.update(0.01)
	_start(system, _at(0, 1))
	system.update(0.01)
	test.check(near.state != "Stopped" and far.state == "Stopped", "furthest stealing measures each candidate in its own region")

	for scatter in [false, true]:
		system = System.new(_project(scatter))
		var instance = _start(system, _at(0, 1))
		system.update(0.2)
		var initial: Array = _of(system, "VoicePlay")
		test.check(not initial.is_empty() and initial.all(func(play): return play.attributes.region == 1), "initial voices use the instance region (scatter=%s)" % scatter)
		system.commands.clear()
		instance.set_3d_attributes(_at(0, 2))
		system.update(0.01)
		var changes: Array = _of(system, "VoiceSet3D")
		test.check(changes.size() == initial.size() and changes.all(func(change): return change.attributes.region == 2), "region change reaches every live voice (scatter=%s)" % scatter)
		for change in changes:
			var play = initial.filter(func(p): return p.voice == change.voice)
			test.check(play.size() == 1 and play[0].attributes.position == change.attributes.position, "region change keeps each voice's position (scatter=%s)" % scatter)

func _recordings(test: Object) -> void:
	var project = _project()
	var system = System.new(project, 17)
	var recorder = Recorder.new(system)
	# Above float's exact integer range: region IDs must never be packed into the coordinate vector.
	var a = 16777217
	var b = 2147483647
	system.set_listener(0, _at(0, a))
	system.set_listener(1, _at(0, b))
	var instance = _start(system, _at(0, a))
	system.update(0.05)
	system.commands.clear()
	instance.set_3d_attributes(_at(0, b))
	system.update(0.05)
	system.commands.clear()
	var recording: Dictionary = recorder.stop()
	var loaded: Dictionary = RecordingJson.deserialize(RecordingJson.serialize(recording))
	var moves: Array = []
	for tick in loaded.ticks:
		moves.append_array(tick.calls.filter(func(call): return call.method == "Set3DAttributes"))
	test.check(moves.size() == 2 and int(moves[0].region) == a and int(moves[1].region) == b, "recordings keep exact integer regions beside the coordinates")
	test.check(Replayer.compare(recording, Replayer.replay(loaded, project)) == null, "region changes replay identically")
	moves[1].region = a
	test.check(Replayer.compare(recording, Replayer.replay(loaded, project)) != null, "a different replayed region diverges")
	var legacy: Dictionary = RecordingJson.deserialize('{"ticks":[{"calls":[{"method":"SetListener","vector":[0,0,0,0,0,0,0,0,-1,0,1,0]}]}]}')
	test.check(int(legacy.ticks[0].calls[0].get("region", 0)) == 0 and not RecordingJson.serialize(legacy).contains('"region"'), "legacy recordings load as region 0 and serialize unchanged")
	var attributes = {"position": Vector3.ZERO, "velocity": Vector3.ZERO, "forward": Vector3.FORWARD, "up": Vector3.UP}
	test.check(CommandText.format({"type": "VoiceSet3D", "voice": 4, "attributes": attributes}) == "VoiceSet3D(4, p=[0 0 0] v=[0 0 0] f=[0 0 -1] u=[0 1 0])", "default-region command text is unchanged")
	attributes.region = 3
	test.check(CommandText.format({"type": "VoiceSet3D", "voice": 4, "attributes": attributes}).ends_with(" region=3)"), "nonzero region appears in command text")

# ---- real engine ------------------------------------------------------------------------------------------------

class Region extends RefCounted:
	var id: int
	var viewport: SubViewport
	var host: Node3D
	var listener: AudioListener3D

func _region(driver, id: int) -> Region:
	var region = Region.new()
	region.id = id
	region.viewport = SubViewport.new()
	region.viewport.own_world_3d = true
	region.viewport.audio_listener_enable_3d = true
	region.viewport.size = Vector2i(16, 16)
	_tree.root.add_child(region.viewport)
	region.host = Node3D.new()
	region.viewport.add_child(region.host)
	var camera = Camera3D.new()
	region.host.add_child(camera)
	camera.make_current()
	region.listener = AudioListener3D.new()
	region.host.add_child(region.listener)
	region.listener.make_current()
	driver.register_spatial_region(id, region.host, region.listener)
	return region

static func _play(voice: int, region: int, loop: bool = true, sync: int = 0, spatial: bool = true) -> Dictionary:
	return {"type": "VoicePlay", "voice": voice, "asset": TONE if loop else SHORT, "bus": "Master", "startSeconds": 0.0, "loop": loop,
		"volumeDb": -30.0, "pitch": 1.0, "spatial": spatial, "attributes": {"position": Vector3(0, 0, -4), "region": region},
		"minDistance": 1.0, "maxDistance": 20.0, "syncGroup": sync}

static func _move(voice: int, region: int) -> Dictionary:
	return {"type": "VoiceSet3D", "voice": voice, "attributes": {"position": Vector3(0, 0, -4), "region": region}}

static func _tone(seconds: float) -> AudioStreamWAV:
	var stream = AudioStreamWAV.new()
	stream.format = AudioStreamWAV.FORMAT_16_BITS
	stream.mix_rate = roundi(AudioServer.get_mix_rate())
	var data = PackedByteArray()
	data.resize(roundi(seconds * stream.mix_rate) * 2)
	stream.data = data
	return stream

func _driver_lifecycle(test: Object) -> void:
	_tree = Engine.get_main_loop() as SceneTree
	var assets = Catalog.new()
	assets.register(TONE, _tone(1.0))
	assets.register(SHORT, _tone(0.05))
	var driver = Driver.new(assets)
	_tree.root.add_child(driver)
	var finished: Dictionary = {}
	driver.voice_finished.connect(func(voice): finished[voice] = true)
	var a = _region(driver, 101)
	var b = _region(driver, 102)
	driver.apply([_play(1000, a.id)])
	driver.apply([_play(1001, b.id)])
	driver.apply([_play(1002, a.id, false)])
	test.check(driver.player_of(1000).get_world_3d() == a.host.get_world_3d(), "region A voice plays in A's world")
	test.check(driver.player_of(1001).get_world_3d() == b.host.get_world_3d(), "region B voice plays in B's world")
	test.check(driver.player_of(1000).get_world_3d() != driver.player_of(1001).get_world_3d(), "same-coordinate voices in different regions do not share a world")
	# Engine capability, measured without Dirge: does a pause requested before the deferred physics registration
	# survive it? The native-pause checks below are mandatory exactly when it does.
	var probe = AudioStreamPlayer3D.new()
	probe.stream = assets.get_looping_stream(TONE)
	probe.volume_db = -80.0
	_tree.root.add_child(probe)
	probe.play(0.25)
	probe.stream_paused = true
	var paused_start = _play(1003, a.id)
	paused_start.startSeconds = 0.25
	driver.apply([paused_start])
	driver.apply([{"type": "VoiceSetPaused", "voice": 1003, "paused": true}])
	driver.apply([_move(1003, b.id)])
	await _tree.create_timer(0.3).timeout
	for _i in range(4):
		await _tree.physics_frame
	var persists: bool = probe.stream_paused
	probe.queue_free()
	print("DIRGE_NATIVE_PENDING_PAUSE probe: %s" % ("engine persists pending pause; native-pause checks are mandatory" if persists else "engine lacks pending-pause persistence; native-pause checks will be skipped"))
	var native_checks: Array = [0]
	var native_pause = func(condition: bool, message: String) -> void:
		native_checks[0] += 1
		if persists:
			test.check(condition, message)

	test.check(finished.has(1002) and driver.player_of(1002) == null, "natural spatial finish releases the voice")
	test.check(driver.player_of(1003).get_world_3d() == b.host.get_world_3d(), "pending-start migration reaches the new world")
	native_pause.call(driver.player_of(1003).stream_paused, "pending-start migration keeps native pause after physics registration")
	native_pause.call(absf(driver.player_of(1003).get_playback_position() - 0.25) < 0.01, "paused startup does not advance the native playback clock")
	driver.apply([{"type": "VoiceSetPaused", "voice": 1003, "paused": false}])
	test.check(not driver.player_of(1003).stream_paused, "migrated voice resumes")
	driver.apply([{"type": "VoiceStop", "voice": 1003}])

	var old = driver.player_of(1000)
	test.check(driver.unregister_spatial_region(a.id), "unregistering a registered region succeeds")
	test.check(driver.player_of(1000) == null and finished.has(1000), "withdrawal finishes the region's voices")
	test.check(old.get_parent() == driver and old.stream == null and old.transform == Transform3D.IDENTITY, "withdrawn slot returns clean to the default pool")
	test.check(driver.player_of(1001).get_world_3d() == b.host.get_world_3d(), "withdrawing A leaves B untouched")
	driver.apply([_play(1004, b.id)])
	test.check(driver.player_of(1004) == old and old.get_world_3d() == b.host.get_world_3d(), "pooled A slot becomes a clean B slot")
	driver.apply([_play(1005, a.id)])
	test.check(driver.player_of(1005) == null and finished.has(1005), "unknown region finishes without default fallback")
	driver.apply([_move(1004, a.id)])
	test.check(driver.player_of(1004) == null and finished.has(1004), "migration into an unknown region fails closed")
	test.check(not driver.unregister_spatial_region(a.id), "unregistering an unknown region reports false")

	driver.register_spatial_region(a.id, a.host, a.listener)
	driver.apply([_play(1006, a.id)])
	driver.apply([{"type": "VoiceSetPaused", "voice": 1006, "paused": true}])
	driver.apply([_move(1006, b.id)])
	test.check(driver.player_of(1006).get_world_3d() == b.host.get_world_3d(), "ordinary migration moves the player to the new world")
	native_pause.call(driver.player_of(1006).stream_paused, "ordinary paused migration keeps native pause")
	driver.apply([_move(1006, 0)])
	test.check(driver.player_of(1006).get_parent() == driver, "region 0 restores the legacy host")
	driver.apply([{"type": "VoiceStop", "voice": 1006}])
	test.check(driver.player_of(1006) == null, "stop releases a migrated voice")
	assets.register(MISSING, null)
	var failed = _play(1007, a.id, false)
	failed.asset = MISSING
	driver.apply([failed])
	test.check(driver.player_of(1007) == null and finished.has(1007), "asset failure releases region ownership")
	test.check(old.get_parent() == driver and old.stream == null, "failed slot keeps no native world or stream")
	assets.evict(MISSING)

	driver.apply([_play(1008, a.id, true, 41), _play(1009, b.id, true, 41)])
	test.check(driver.player_of(1008) != driver.player_of(1009), "sync groups never merge distinct worlds")
	driver.apply([{"type": "VoiceStop", "voice": 1008}, {"type": "VoiceStop", "voice": 1009}])
	driver.apply([_play(1010, a.id, true, 42), _play(1011, a.id, true, 42)])
	test.check(driver.player_of(1010) == driver.player_of(1011) and driver.is_synchronized(1010), "same-world sync layers still group")
	driver.apply([{"type": "VoiceSetPaused", "voice": 1010, "paused": true}])
	driver.apply([_move(1010, b.id)])
	test.check(driver.player_of(1010).get_world_3d() == b.host.get_world_3d(), "migrating sync layer moves to the new world")
	test.check(driver.player_of(1011).get_world_3d() == a.host.get_world_3d(), "sync sibling stays in its own world")
	native_pause.call(driver.player_of(1010).stream_paused and driver.player_of(1011).stream_paused, "sync split keeps native pause on both layers")
	driver.apply([{"type": "VoiceSetPaused", "voice": 1010, "paused": false}])
	test.check(not driver.player_of(1010).stream_paused, "split layer resumes")
	native_pause.call(driver.player_of(1011).stream_paused, "split layer resume leaves its sibling paused")

	# A listener can exit while its host/viewport is traversing children: retire native slots, never reparent them.
	a.host.remove_child(a.listener)
	test.check(driver.player_of(1011) == null and finished.has(1011), "listener exit finishes the region's voices")
	a.host.add_child(a.listener)
	a.listener.make_current()
	driver.register_spatial_region(a.id, a.host, a.listener)
	driver.apply([_play(1012, a.id)])
	test.check(driver.player_of(1012).get_world_3d() == a.host.get_world_3d(), "re-registered region routes to its world")
	_tree.root.remove_child(a.viewport)
	test.check(driver.player_of(1012) == null and finished.has(1012), "world subtree exit finishes the region's voices")
	a.viewport.queue_free()

	b.viewport.world_3d = World3D.new()
	driver.apply([])
	test.check(driver.player_of(1001) == null and driver.player_of(1010) == null, "replaced world withdraws old-world voices before the next batch")
	driver.apply([_play(1013, 0)])
	test.check(driver.player_of(1013).get_parent() == driver, "default route is unchanged")
	driver.apply([_play(1014, -99, true, 0, false)])
	test.check(driver.player_of(1014) is AudioStreamPlayer and driver.player_of(1014).get_parent() == driver, "nonspatial audio ignores region registration")

	print("DIRGE_NATIVE_PENDING_PAUSE engine %s: %d native-pause assertions %s" % ["persists pending pause" if persists else "lacks pending-pause persistence", native_checks[0], "enforced" if persists else "skipped"])
	driver.shutdown()
	driver.queue_free()
	b.viewport.queue_free()
	await _tree.process_frame
