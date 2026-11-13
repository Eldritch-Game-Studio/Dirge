extends RefCounted

const Model = preload("res://addons/dirge/model/Project/project_model.gd")
const System = preload("res://addons/dirge/model/Runtime/dirge_system.gd")
const Recorder = preload("res://addons/dirge/model/Profiling/recorder.gd")
const Recording = preload("res://addons/dirge/model/Profiling/recording.gd")
const RecordingJson = preload("res://addons/dirge/model/Profiling/recording_json.gd")
const Replayer = preload("res://addons/dirge/model/Profiling/replayer.gd")
const CommandText = preload("res://addons/dirge/model/Profiling/command_text.gd")

func run(test: Object) -> void:
	_compare(test)
	_json_and_commands(test)
	_session(test)
	_suppression(test)
	_ownership(test)

func _compare(test: Object) -> void:
	var a: Dictionary = Recording.create()
	var b: Dictionary = Recording.create()
	a.ticks.append(Recording.tick())
	b.ticks.append(Recording.tick())
	a.ticks[0].commands.append("X")
	test.check(Replayer.compare(a, b) == {"tick": 0, "index": 0, "expected": "X", "actual": "<no command>"}, "Comparison reports missing command")
	b.ticks[0].commands.append("X")
	test.check(Replayer.compare(a, b) == null, "Matching command streams compare equal")
	b.ticks[0].commands.append("Y")
	test.check(Replayer.compare(a, b).actual == "Y", "Comparison reports extra command")
	b.ticks[0].commands = ["Z", "Y"]
	test.check(Replayer.compare(a, b).index == 0, "Comparison returns first differing command")
	b.ticks[0].commands = ["X"]
	b.ticks.append(Recording.tick())
	test.check(Replayer.compare(a, b) == {"tick": 1, "index": 0, "expected": "<end of recording>", "actual": "<tick>"}, "Comparison reports first extra tick")

func _json_and_commands(test: Object) -> void:
	var text: String = '{"projectName":"Legacy","seed":17,"locale":"en","ticks":[{"dt":0.125,"time":0,"calls":[{"method":"SetBusVolume","target":"Mixer","handle":-1,"result":-1,"name":"Music","value":-6,"flag":false,"mode":0}],"programmerSounds":[],"commands":["BusSetVolume(Music, -6)"],"ended":[],"instanceCount":0,"realVoiceCount":0}]}'
	var loaded: Dictionary = RecordingJson.deserialize(text)
	test.check(loaded.seed == 17 and loaded.platform == null and loaded.ticks[0].calls[0].vector == null, "Existing C# recording JSON loads omitted nullable fields")
	var serialized: String = RecordingJson.serialize(loaded)
	test.check(not serialized.contains('"platform"') and not serialized.contains('"vector"'), "Recording JSON omits nulls")
	test.check(RecordingJson.serialize(RecordingJson.deserialize(serialized)) == serialized, "Recording JSON roundtrip is stable")
	test.check(Replayer.compare(loaded, RecordingJson.deserialize(serialized)) == null, "JSON preserves command oracle")
	var attributes: Dictionary = {"position": Vector3(1, 2, 3), "velocity": Vector3(4, 5, 6), "forward": Vector3.FORWARD, "up": Vector3.UP}
	test.check(Recording.unpack(Recording.pack(attributes)) == attributes and Recording.unpack(null).forward == Vector3.FORWARD, "API spatial packing and missing-vector origin")
	var cases: Array = [
		[{"type": "VoiceStop", "voice": 2}, "VoiceStop(2)"],
		[{"type": "VoiceSeek", "voice": 2, "seconds": 0.5}, "VoiceSeek(2, 0.5)"],
		[{"type": "VoiceSetVolume", "voice": 2, "volumeDb": -6}, "VoiceSetVolume(2, -6)"],
		[{"type": "VoiceSetPitch", "voice": 2, "pitch": 1.5}, "VoiceSetPitch(2, 1.5)"],
		[{"type": "VoiceSetPaused", "voice": 2, "paused": true}, "VoiceSetPaused(2, True)"],
		[{"type": "BusSetVolume", "bus": "Music", "volumeDb": -6}, "BusSetVolume(Music, -6)"],
		[{"type": "BusSetMute", "bus": "Music", "mute": true}, "BusSetMute(Music, True)"],
		[{"type": "BusSetSolo", "bus": "Music", "solo": false}, "BusSetSolo(Music, False)"],
		[{"type": "BusSetBypass", "bus": "Music", "bypass": true}, "BusSetBypass(Music, True)"],
		[{"type": "EffectSetEnabled", "bus": "Music", "effectIndex": 1, "enabled": false}, "EffectSetEnabled(Music, 1, False)"],
		[{"type": "EffectSetParameter", "bus": "Music", "effectIndex": 1, "parameter": "pitch_scale", "value": 0.5}, "EffectSetParameter(Music, 1, pitch_scale, 0.5)"],
		[{"type": "VoicePlay", "voice": 2, "asset": "res://a.ogg", "bus": "Music", "loop": true}, "VoicePlay(2, res://a.ogg, Music, 0, loop, 0 dB, 1x, 2d, sync 0)"]]
	for pair in cases:
		test.check(CommandText.format(pair[0]) == pair[1], "Command text format " + pair[0].type)
	test.check(CommandText.format({"type": "VoiceSet3D", "voice": 2, "attributes": attributes}) == "VoiceSet3D(2, p=[1 2 3] v=[4 5 6] f=[0 0 -1] u=[0 1 0])", "Spatial command text uses invariant vectors")

func _project() -> Dictionary:
	var p: Dictionary = Model.new_project("Recording regression")
	var master: Dictionary = Model.create("Bus", {"name": "Master"})
	var music: Dictionary = Model.create("Bus", {"name": "Music", "parent": master.id})
	var vca: Dictionary = Model.create("Vca", {"name": "Main"})
	music.vcas.append(vca.id)
	p.mixer.buses = [master, music]
	p.mixer.vcas = [vca]
	p.globalParameters.append(Model.create("ParameterDefinition", {"name": "Intensity"}))
	p.snapshots.append(Model.create("Snapshot", {"name": "Duck", "intensity": {"attack": 0.2, "release": 0.3}, "values": [{"target": Model.property_ref("BusVolumeDb", music.id), "value": -12.0}]}))
	var loop: Dictionary = Model.create("AudioEvent", {"path": "Loop", "bus": music.id, "macros": {"persistent": true}})
	loop.tracks.append(Model.create("Track", {"instruments": [Model.create("Clip", {"asset": "res://loop.ogg", "loop": true})]}))
	var shot: Dictionary = Model.create("AudioEvent", {"path": "Shot", "bus": music.id, "macros": {"is3D": true}})
	shot.tracks.append(Model.create("Track", {"instruments": [Model.create("Multi", {"volumeRandomDb": 3.0, "pitchRandom": 0.1, "entries": [{"asset": "res://a.ogg", "weight": 1.0}, {"asset": "res://b.ogg", "weight": 1.0}]})]}))
	var programmer: Dictionary = Model.create("AudioEvent", {"path": "Programmer", "bus": music.id})
	programmer.tracks.append(Model.create("Track", {"instruments": [Model.create("Programmer", {"key": "line"})]}))
	var unknown: Dictionary = Model.create("AudioEvent", {"path": "Unknown", "bus": music.id})
	unknown.tracks.append(Model.create("Track", {"instruments": [Model.create("Clip", {"asset": "res://unknown.ogg"})]}))
	p.events = [loop, shot, programmer, unknown]
	p.assetLengths = {"res://loop.ogg": 2.0, "res://a.ogg": 0.4, "res://b.ogg": 0.5, "res://voice.ogg": 0.6}
	return p

func _session(test: Object) -> void:
	var p: Dictionary = _project()
	var system = System.new(p, 7, null, "linux")
	system.locale = "en"
	var recorder = Recorder.new(system)
	system.max_real_voices = 8
	var loop = system.get_event("Loop").create_instance()
	loop.start()
	var snapshot
	var unknown_voice: Array = []
	for tick in 100:
		system.set_listener(0, {"position": Vector3(tick * 0.01, 0, 0), "velocity": Vector3.RIGHT, "forward": Vector3.FORWARD, "up": Vector3.UP})
		if tick % 10 == 0:
			var shot = system.get_event("Shot").create_instance()
			shot.set_3d_attributes({"position": Vector3(0, 0, tick % 20), "velocity": Vector3.ZERO, "forward": Vector3.FORWARD, "up": Vector3.UP})
			shot.set_pitch(1.2)
			shot.set_volume(-2)
			shot.start()
			shot.release()
		match tick:
			5: system.set_global_parameter("Intensity", 1.0, true)
			10: snapshot = system.mixer.start_snapshot("Duck")
			15: snapshot.intensity = 0.5
			20:
				var programmer = system.get_event("Programmer").create_instance()
				programmer.callback.connect(func(_instance, event):
					if event.type == "ProgrammerSoundCallback": event.asset = "res://voice.ogg")
				programmer.start()
				programmer.release()
			25: snapshot.stop()
			30: system.mixer.set_bus_volume("Music", -3)
			31: system.mixer.set_bus_mute("Music", true)
			32: system.mixer.set_bus_mute("Music", false)
			33: system.mixer.set_bus_solo("Music", true)
			34: system.mixer.set_vca_volume("Main", -2)
			35: loop.set_paused(true)
			36: loop.set_paused(false)
			40: loop.timeline_position = 0.75
			45: loop.set_parameter("Intensity", 0.2, true)
			50:
				var unknown = system.get_event("Unknown").create_instance()
				unknown.callback.connect(func(_instance, event):
					if event.type == "SoundPlayedCallback": unknown_voice.append(event.voice))
				unknown.start()
				unknown.release()
			60:
				if not unknown_voice.is_empty(): system.feedback({"type": "VoiceFinished", "voice": unknown_voice[0]})
			70: loop.key_off()
			80: system.stop_all("Immediate")
		system.update(0.05)
		system.commands.clear()
	var recording: Dictionary = recorder.stop()
	test.check(recording.ticks.size() == 100 and recording.platform == "linux" and recording.locale == "en", "Recorder captures all tick metadata")
	test.check(recording.ticks[20].programmerSounds.size() == 1 and recording.ticks[20].programmerSounds[0].asset == "res://voice.ogg", "Recorder captures callback programmer resolution")
	test.check(not unknown_voice.is_empty(), "Recording session exercises driver-free unknown-length feedback")
	var replay: Dictionary = Replayer.replay(recording, p)
	test.check(Replayer.compare(recording, replay) == null, "Same-seed full API session replays without divergence")
	test.check(Replayer.compare(recording, Replayer.replay(recording, p, 8)) != null, "Different random seed causes first divergence")
	var loaded: Dictionary = RecordingJson.deserialize(RecordingJson.serialize(recording))
	test.check(Replayer.compare(recording, Replayer.replay(loaded, p)) == null, "Serialized session replays identically")
	var ended: int = 0
	for tick in recording.ticks: ended += tick.ended.size()
	test.check(ended >= 10, "One-shots, snapshot and stopped instances report ends")
	test.check(system.recorder == null, "Recorder detaches on stop")
	system.update(0.1)
	test.check(recording.ticks.size() == 100, "Stopped recorder remains unchanged")

func _suppression(test: Object) -> void:
	var p: Dictionary = _project()
	var system = System.new(p)
	var recorder = Recorder.new(system)
	recorder.enter_internal()
	var internal = system.get_event("Loop").create_instance()
	internal.start()
	recorder.enter_internal()
	system.mixer.set_bus_volume("Music", -1)
	recorder.exit_internal()
	recorder.exit_internal()
	recorder.exit_internal()
	var instance = system.get_event("Shot").create_instance()
	instance.start()
	system.update(0.1)
	system.commands.clear()
	var recording: Dictionary = recorder.stop()
	test.check(recording.ticks[0].calls.size() == 2 and recording.ticks[0].calls[0].method == "CreateInstance" and recording.ticks[0].calls[1].method == "Start", "Nested internal calls suppressed; depth cannot underflow")
	test.check(recorder.handle_of(internal) == -1 and recorder.handle_of(instance) == 0, "Only exposed objects receive recorder handles")

func _ownership(test: Object) -> void:
	var system = System.new(_project())
	var recorder = Recorder.new(system)
	var instance = system.get_event("Loop").create_instance()
	instance.start()
	system.update(0.1)
	var system_ref: WeakRef = weakref(system)
	instance = null
	system = null
	test.check(system_ref.get_ref() == null, "Recorder and mixer backreferences do not retain system")
	test.check(recorder.stop().ticks.size() == 1, "Recorder can detach after owning system is freed")
