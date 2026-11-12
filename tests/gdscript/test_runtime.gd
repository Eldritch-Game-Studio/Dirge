extends RefCounted

const System = preload("res://addons/dirge/model/Runtime/dirge_system.gd")
const Bank = preload("res://addons/dirge/model/Runtime/parameter_bank.gd")
const Spatial = preload("res://addons/dirge/model/Runtime/spatial.gd")
const Recorder = preload("res://addons/dirge/model/Profiling/recorder.gd")
const Replayer = preload("res://addons/dirge/model/Profiling/replayer.gd")
const RecordingJson = preload("res://addons/dirge/model/Profiling/recording_json.gd")
var _serial: int = 1

func _id() -> String:
	var result = "%032x" % _serial
	_serial += 1
	return result

func _project() -> Dictionary:
	return {"events": [], "globalParameters": [], "assetLengths": {}, "snapshots": [], "audioTables": [],
		"mixer": {"buses": [{"id": _id(), "name": "Master"}], "vcas": []}}

func _event(project: Dictionary, path: String = "Test/Event") -> Dictionary:
	var event = {"id": _id(), "path": path, "macros": {}, "localParameters": [], "tracks": [], "markers": [], "tempo": [], "automation": [], "modulators": []}
	project.events.append(event)
	return event

func _instrument(event: Dictionary, kind: String = "Clip", start: float = 0, length: float = 0) -> Dictionary:
	if event.tracks.is_empty():
		event.tracks.append({"id": _id(), "volumeDb": 0.0, "instruments": []})
	var instrument = {"id": _id(), "$type": kind, "placement": {"start": start, "length": length}, "asset": "res://tone.wav"}
	event.tracks[0].instruments.append(instrument)
	return instrument

func _parameter(event: Dictionary, name: String = "P", maximum: float = 1.0) -> Dictionary:
	var parameter = {"id": _id(), "name": name, "minimum": 0.0, "maximum": maximum, "default": 0.0}
	event.localParameters.append(parameter)
	return parameter

func _marker(event: Dictionary, kind: String, position: float, fields: Dictionary = {}) -> Dictionary:
	var marker = {"id": _id(), "$type": kind, "position": position}
	marker.merge(fields, true)
	event.markers.append(marker)
	return marker

func _minimal(length: float = 10.0) -> Dictionary:
	var project = _project()
	_instrument(_event(project))
	if length > 0:
		project.assetLengths["res://tone.wav"] = length
	return project

func _of(system, type: String) -> Array:
	return system.commands.filter(func(command): return command.type == type)

func _first(system, type: String) -> Dictionary:
	var commands = _of(system, type)
	return commands[0] if not commands.is_empty() else {}

func _near(a: float, b: float, epsilon: float = 0.000001) -> bool:
	return absf(a - b) <= epsilon

func run(test: Object) -> void:
	_parameters(test)
	_lifecycle_feedback(test)
	_gain_pause_fades(test)
	_sheets_conditions(test)
	_loops_sustain_markers(test)
	_transitions(test)
	_clocked_timing(test)
	_quantized_starts(test)
	_native_quantized_alignment(test)
	_stealing_limits(test)
	_playlists_scatter(test)
	_nested_commands_programmer(test)
	_spatial_automation_modulation(test)
	_snapshots_delay_silence(test)
	_determinism(test)
	_command_edges_and_ownership(test)
	_tempo_and_instrument_modulation(test)

func _native_quantized_alignment(test) -> void:
	for overshoot in [0.0210284, 0.125]:
		var project = _project()
		var conductor_event = _event(project, "Music/Conductor")
		conductor_event.isConductor = true
		conductor_event.tempo.append({"position": 0, "bpm": 120})
		_instrument(conductor_event)
		var cue_event = _event(project, "Music/Cue")
		cue_event.macros.startQuantization = "Beat"
		_instrument(cue_event)
		project.assetLengths["res://tone.wav"] = 10
		var system = System.new(project)
		var recorder = Recorder.new(system)
		var conductor = system.get_event("Music/Conductor").create_instance()
		conductor.start()
		system.update(0)
		system.commands.clear()
		system.update(0.49)
		system.commands.clear()
		var cue = system.get_event("Music/Cue").create_instance()
		cue.start()
		system.update(0.01 + overshoot)
		var play = _first(system, "VoicePlay")
		test.check(_near(cue.timeline_position, overshoot), "quantized cue retains remaining model tick")
		test.check(not play.is_empty() and _near(play.startSeconds, fmod(conductor.timeline_position, 0.5)),
			"unclocked quantized native onset aligns after a slow frame")
		var recording = recorder.stop()
		test.check(Replayer.compare(recording, Replayer.replay(recording, project)) == null,
			"quantized native catchup replays identically")

		project = _project()
		var event = _event(project, "Music/Transition")
		_instrument(event, "Clip", 0, 4).asset = "res://calm.wav"
		_instrument(event, "Clip", 4, 4).asset = "res://combat.wav"
		project.assetLengths["res://calm.wav"] = 4
		project.assetLengths["res://combat.wav"] = 4
		var destination = _marker(event, "Destination", 4)
		_marker(event, "Transition", 0.5, {"destination": destination.id, "quantization": "None"})
		system = System.new(project)
		recorder = Recorder.new(system)
		var music = system.get_event("Music/Transition").create_instance()
		music.start()
		system.update(0)
		system.commands.clear()
		system.update(0.5 + overshoot)
		var combat = _of(system, "VoicePlay").filter(func(command): return command.asset == "res://combat.wav")
		test.check(_near(music.timeline_position, 4 + overshoot), "transition retains the remaining model tick")
		test.check(combat.size() == 1 and _near(combat[0].startSeconds, overshoot),
			"transition-entered native layer catches up after a slow frame")
		recording = recorder.stop()
		test.check(Replayer.compare(recording, Replayer.replay(recording, project)) == null,
			"transition native catchup replays identically")

func _parameters(test) -> void:
	var definition = {"id": "p", "name": "Speed", "minimum": 0, "maximum": 10, "default": 2, "seekSpeedUp": 4, "seekSpeedDown": 1}
	var bank = Bank.new([definition])
	test.check(bank.get_state("p") == bank.get_state("Speed"), "bank resolves both ID and name")
	bank.set_value("Speed", 8)
	bank.advance(0.5)
	test.check(bank.get_value("p") == 4 and bank.get_state("p").velocity == 4, "asymmetric upward seek and velocity")
	bank.set_value("p", -10)
	bank.advance(1)
	test.check(bank.get_value("p") == 3 and bank.get_state("p").target == 0, "downward seek clamps target")
	bank.set_value("p", 9, true)
	bank.advance(0.5)
	test.check(bank.get_state("p").velocity == 12, "velocity includes immediate mutation since preceding tick")
	bank.advance(0)
	test.check(bank.get_state("p").velocity == 0, "zero delta velocity is zero")
	var discrete = Bank.ParameterState.new({"kind": "Discrete", "minimum": 0, "maximum": 5})
	discrete.set_value(2.5, true)
	test.check(discrete.value == 2, "discrete midpoint rounds nearest-even like C#")
	discrete.set_value(3.5, true)
	test.check(discrete.value == 4, "discrete odd midpoint rounds upward")
	var negative = Bank.ParameterState.new({"kind": "Discrete", "minimum": -5, "maximum": 0})
	negative.set_value(-2.5, true)
	test.check(negative.value == -2, "negative discrete midpoint also rounds nearest-even")
	var project = _minimal()
	var event = project.events[0]
	_parameter(event, "Label", 3).merge({"kind": "Labeled", "minimum": 1, "labels": ["Low", "Mid", "High"]}, true)
	project.globalParameters.append({"id": _id(), "name": "Global", "maximum": 10})
	var system = System.new(project)
	var instance = system.get_event(event.id).create_instance()
	instance.set_parameter_by_label("Label", "High")
	instance.set_parameter("Global", 7, true)
	instance.start()
	system.update(0)
	test.check(instance.get_parameter("Label") == 3 and system.get_global_parameter("Global") == 7, "labels honor minimum and local setters fall through to globals")

func _lifecycle_feedback(test) -> void:
	var system = System.new(_minimal(0.05))
	var description = system.get_event("Test/Event")
	var instance = description.create_instance()
	var callbacks: Array = []
	instance.callback.connect(func(_owner, payload): callbacks.append(payload.type))
	instance.start()
	instance.release()
	system.update(0.1)
	test.check(_of(system, "VoicePlay").size() == 1 and _first(system, "VoicePlay").get("startSeconds", -1) == 0, "one-shot shorter than tick still plays from zero")
	test.check(instance.state == "Playing", "short one-shot survives its first emission")
	system.commands.clear()
	system.update(0.1)
	test.check(instance.state == "Stopped" and system.instance_count == 0, "catalog end stops and auto-releases")
	test.check(callbacks == ["StartedCallback", "SoundPlayedCallback", "SoundStoppedCallback", "StoppedCallback"], "lifecycle callback order")
	var recycled = description.create_instance()
	test.check(recycled == instance and recycled.callback.get_connections().is_empty(), "released instances pooled and callbacks cleared")
	var unknown = System.new(_minimal(0))
	var live = unknown.get_event("Test/Event").create_instance()
	live.start()
	unknown.update(0)
	var voice = _first(unknown, "VoicePlay").get("voice", -1)
	unknown.feedback({"type": "VoiceFinished", "voice": voice})
	test.check(live.state == "Playing", "feedback deferred until update")
	unknown.update(0)
	test.check(live.state == "Stopped", "unknown-length voice ends from deferred feedback")
	var known = System.new(_minimal())
	var playing = known.get_event("Test/Event").create_instance()
	playing.start()
	known.update(0)
	voice = _first(known, "VoicePlay").get("voice", -1)
	known.feedback({"type": "VoiceFinished", "voice": voice})
	known.update(0.1)
	test.check(playing.state == "Playing", "late native finished ignored when length is model-owned")
	known.feedback({"type": "VoiceFailed", "voice": voice, "reason": "missing"})
	known.update(11)
	test.check(playing.state == "Stopped", "failed native voice dropped even with catalog length")
	var persistent_project = _minimal()
	persistent_project.events[0].macros.persistent = true
	var persistent = System.new(persistent_project)
	var retained = persistent.get_event("Test/Event").create_instance()
	retained.start()
	persistent.update(0)
	persistent.update(20)
	test.check(retained.state == "Playing", "persistent event outlives its voices")
	retained.stop("Immediate")
	test.check(retained.state == "Stopped", "immediate stop finishes synchronously")

func _gain_pause_fades(test) -> void:
	var project = _minimal()
	var event = project.events[0]
	var clip = event.tracks[0].instruments[0]
	event.macros = {"volumeDb": -3, "pitch": 1.2}
	event.tracks[0].volumeDb = -2
	clip.volumeDb = -4
	clip.pitch = 1.5
	clip.fadeOut = 1
	clip.syncGroup = 7
	var system = System.new(project)
	var instance = system.get_event("Test/Event").create_instance()
	instance.set_volume(-1)
	instance.set_pitch(0.5)
	instance.start()
	system.update(0)
	var play = _first(system, "VoicePlay")
	test.check(_near(play.get("volumeDb", 999), -10) and _near(play.get("pitch", 0), 0.9), "track/instrument/event/instance gain and pitch chain")
	test.check(play.get("syncGroup", 0) == 7, "synchronized layer group forwarded")
	system.commands.clear()
	instance.set_paused(true)
	system.update(0.5)
	test.check(instance.timeline_position == 0 and _first(system, "VoiceSetPaused").get("paused", false), "pause freezes timeline and emits native pause")
	instance.set_paused(false)
	system.update(0)
	instance.stop()
	system.commands.clear()
	system.update(0.5)
	test.check(instance.state == "Stopping" and _near(_first(system, "VoiceSetVolume").get("volumeDb", 999), -16.0205999), "allow-fade stop emits half-amplitude dB ramp")
	system.update(0.5)
	test.check(instance.state == "Stopped" and _of(system, "VoiceStop").size() == 1, "fade completion emits one stop")
	var fade_project = _minimal()
	fade_project.events[0].tracks[0].instruments[0].fadeIn = 1
	var fading = System.new(fade_project)
	fading.get_event("Test/Event").create_instance().start()
	fading.update(0)
	test.check(_first(fading, "VoicePlay").get("volumeDb", 0) == -80, "fade-in begins at silent floor")
	fading.commands.clear()
	fading.update(0.5)
	test.check(_near(_first(fading, "VoiceSetVolume").get("volumeDb", 999), -6.0205999), "fade-in progresses on voice age")

func _sheets_conditions(test) -> void:
	for asynchronous in [false, true]:
		var project = _minimal()
		var event = project.events[0]
		event.macros.persistent = true
		var parameter = _parameter(event)
		parameter.seekSpeedUp = 1
		var clip = event.tracks[0].instruments[0]
		clip.placement = {"parameter": parameter.id, "start": 0.5, "length": 0.5}
		clip["async"] = asynchronous
		var system = System.new(project)
		var instance = system.get_event(event.id).create_instance()
		instance.start()
		system.update(0)
		instance.set_parameter("P", 1)
		system.update(0.25)
		test.check(_of(system, "VoicePlay").is_empty(), "sheet waits for smoothed effective value")
		system.update(0.25)
		test.check(_of(system, "VoicePlay").size() == 1, "sheet triggers at inclusive lower bound")
		system.update(0.5)
		test.check(_of(system, "VoiceStop").is_empty(), "sheet includes upper endpoint at parameter maximum")
		system.commands.clear()
		instance.set_parameter("P", 0, true)
		system.update(0)
		test.check(_of(system, "VoiceStop").size() == (0 if asynchronous else 1), "sheet exit respects async/cut")
	var project = _minimal()
	var event = project.events[0]
	event.macros.persistent = true
	var parameter = _parameter(event)
	var clip = event.tracks[0].instruments[0]
	clip.conditions = [{"parameter": parameter.id, "minimum": 0.5, "maximum": 1}]
	var system = System.new(project)
	var instance = system.get_event(event.id).create_instance()
	instance.start()
	system.update(0)
	instance.set_parameter("P", 1, true)
	system.update(0.1)
	test.check(_of(system, "VoicePlay").is_empty(), "condition failure consumes region entry, does not retrigger while already inside")
	instance.timeline_position = 11
	system.update(0)
	instance.timeline_position = 0
	system.update(0)
	test.check(_of(system, "VoicePlay").size() == 1, "condition reevaluates on a new region crossing")

func _loops_sustain_markers(test) -> void:
	var project = _minimal(2)
	var event = project.events[0]
	_marker(event, "Loop", 0, {"length": 2})
	_marker(event, "Destination", 0.5, {"name": "Half"})
	event.tempo = [{"position": 0, "bpm": 120, "beatsPerBar": 4}]
	var system = System.new(project)
	var instance = system.get_event(event.id).create_instance()
	var callbacks: Array = []
	instance.callback.connect(func(_owner, payload): callbacks.append(payload))
	instance.start()
	system.update(0)
	system.commands.clear()
	for _i in range(20):
		system.update(0.1)
	test.check(_near(instance.timeline_position, 0, 1e-9), "epsilon snapping wraps accumulated fractional ticks at exact loop end")
	test.check(_of(system, "VoiceStop").is_empty() and _of(system, "VoicePlay").is_empty() and _of(system, "VoiceSeek").size() == 1, "loop endpoint preserves and reseeks synchronous native voice")
	test.check(callbacks.any(func(c): return c.type == "MarkerCallback" and c.name == "Half"), "destination marker callback emitted")
	test.check(callbacks.any(func(c): return c.type == "BeatCallback" and c.beat == 2 and c.bar == 1), "tempo map beat and bar callback emitted")
	var sustain_project = _minimal()
	_marker(sustain_project.events[0], "SustainPoint", 1)
	var sustain = System.new(sustain_project)
	var held = sustain.get_event("Test/Event").create_instance()
	held.start()
	sustain.update(2)
	test.check(held.state == "Sustaining" and held.timeline_position == 1, "sustain freezes exactly at point")
	sustain.update(3)
	test.check(held.timeline_position == 1, "sustain remains held across ticks")
	held.key_off()
	sustain.update(0.25)
	test.check(held.state == "Playing" and held.timeline_position == 1.25, "key-off resumes without reholding")
	var magnet_project = _minimal()
	_marker(magnet_project.events[0], "Magnet", 2, {"length": 2})
	var magnet_system = System.new(magnet_project)
	var magnet = magnet_system.get_event("Test/Event").create_instance()
	magnet.timeline_position = 3
	test.check(magnet.timeline_position == 2, "stopped seek snaps to first matching magnet")
	magnet.timeline_position = 4
	test.check(magnet.timeline_position == 4, "magnet end is exclusive")

func _transitions(test) -> void:
	var project = _minimal(10)
	var event = project.events[0]
	var destination = _marker(event, "Destination", 8)
	var transition = _marker(event, "Transition", 2, {"destination": destination.id, "transitionLength": 1})
	transition.transitionInstruments = [{"id": _id(), "$type": "Clip", "asset": "res://riser.wav", "placement": {"start": 0}}]
	project.assetLengths["res://riser.wav"] = 5
	var system = System.new(project)
	var instance = system.get_event(event.id).create_instance()
	instance.start()
	system.update(0)
	system.commands.clear()
	system.update(2)
	test.check(instance.is_in_transition and instance.timeline_position == 2 and instance.transition_position == 0, "transition starts local clock and freezes source playhead")
	test.check(_first(system, "VoicePlay").get("asset", "") == "res://riser.wav" and _of(system, "VoiceStop").size() == 1, "transition cuts source and plays riser")
	system.commands.clear()
	system.update(0.5)
	test.check(instance.timeline_position == 2 and instance.transition_position == 0.5, "transition elapsed independent of source clock")
	system.update(0.5)
	test.check(not instance.is_in_transition and _near(instance.timeline_position, 8), "transition lands precisely at destination")
	test.check(_first(system, "VoicePlay").get("startSeconds", -1) == 8, "destination starts source clip at matching offset")
	var quantized = _minimal(20)
	var music = quantized.events[0]
	music.tempo = [{"position": 0, "bpm": 120}]
	var dest = _marker(music, "Destination", 10)
	_marker(music, "Transition", 0.2, {"destination": dest.id, "quantization": "Beat"})
	var root = System.new(quantized)
	var owner = root.get_event(music.id).create_instance()
	owner.start()
	root.update(0.3)
	test.check(_near(owner.timeline_position, 0.3), "quantized transition waits after marker")
	root.update(0.3)
	test.check(_near(owner.timeline_position, 10.1), "quantized transition consumes residual tick after grid")
	var region_project = _minimal(20)
	var region_event = region_project.events[0]
	region_event.tempo = [{"position": 0, "bpm": 60}]
	var parameter = _parameter(region_event)
	var region_dest = _marker(region_event, "Destination", 10)
	_marker(region_event, "TransitionRegion", 0, {"length": 4, "destination": region_dest.id, "quantization": "Beat", "conditions": [{"parameter": parameter.id, "minimum": 1, "maximum": 1}]})
	var region_system = System.new(region_project)
	var region_instance = region_system.get_event(region_event.id).create_instance()
	region_instance.start()
	region_system.update(0.25)
	region_instance.set_parameter("P", 1, true)
	region_system.update(0.85)
	test.check(_near(region_instance.timeline_position, 10.1), "armed transition region retries conditions on subsequent grid points")

func _clocked_timing(test) -> void:
	var project = _minimal(180)
	var second = _event(project, "Test/Other")
	_instrument(second)
	var clock = {"elapsed": 0.0}
	var system = System.new(project, 0, null, "", func(): return clock.elapsed)
	var running = system.get_event(second.id).create_instance()
	running.start()
	system.update(0)
	system.commands.clear()
	clock.elapsed = 1.39733
	var music = system.get_event("Test/Event").create_instance()
	var observed = {"position": -1.0}
	music.callback.connect(func(owner, payload):
		if payload.type == "SoundPlayedCallback": observed.position = owner.timeline_position)
	music.start()
	music.timeline_position = 0.27
	system.update(1.42233)
	test.check(_near(music.timeline_position, 0.295, 1e-9) and _near(observed.position, 0.295, 1e-9), "start/seek excludes interval before mutation including callback position")
	test.check(_near(_first(system, "VoicePlay").get("startSeconds", -1), 0.295, 1e-9), "native onset matches corrected model position")
	test.check(_near(running.timeline_position, 1.42233, 1e-9), "unmutated instance receives full tick")
	system.commands.clear()
	system.update(0.1)
	test.check(_near(music.timeline_position, 0.395, 1e-9) and _of(system, "VoiceSeek").is_empty(), "mutation offset consumed once")
	clock.elapsed = 1.4
	music.timeline_position = 125
	clock.elapsed = 1.5
	music.timeline_position = 126
	system.commands.clear()
	system.update(1.525)
	test.check(_near(music.timeline_position, 126.025, 1e-9), "latest seek captures its own boundary")
	test.check(_near(_first(system, "VoiceSeek").get("seconds", -1), 126.025, 1e-9) and _of(system, "VoicePlay").is_empty(), "existing voice seeks after post-boundary advancement without replacement")
	for before in [0.0, 10.0]:
		var cue_system = System.new(_minimal(0.05), 0, null, "", func(): return before)
		var cue = cue_system.get_event("Test/Event").create_instance()
		cue.start()
		cue.release()
		cue_system.update(before)
		test.check(cue.state == "Playing" and cue.timeline_position == 0 and _first(cue_system, "VoicePlay").get("startSeconds", -1) == 0, "short cue survives entirely pre-start frame stall")
		cue_system.update(1.05)
		test.check(cue.state == "Stopped" and cue_system.instance_count == 0, "short clocked cue still completes and releases")
	var unclocked = System.new(_minimal(180))
	var deterministic = unclocked.get_event("Test/Event").create_instance()
	deterministic.start()
	deterministic.timeline_position = 10
	unclocked.update(0.25)
	test.check(deterministic.timeline_position == 10.25 and _first(unclocked, "VoicePlay").get("startSeconds", -1) == 10, "unclocked deterministic tick-boundary semantics retained")
	var jump_project = _minimal(180)
	var jump_event = jump_project.events[0]
	var destination = _marker(jump_event, "Destination", 50)
	_marker(jump_event, "TransitionRegion", 10, {"length": 2, "destination": destination.id, "quantization": "None"})
	clock.elapsed = 0
	var jump_system = System.new(jump_project, 0, null, "", func(): return clock.elapsed)
	var jumper = jump_system.get_event(jump_event.id).create_instance()
	jumper.start()
	jump_system.update(0)
	jump_system.commands.clear()
	clock.elapsed = 1.4
	jumper.timeline_position = 10.5
	jump_system.update(1.425)
	test.check(_near(jumper.timeline_position, 50.025, 1e-9) and _near(_first(jump_system, "VoiceSeek").get("seconds", -1), 50.025, 1e-9), "Begin transition-region jump defers native seek through post-boundary time")
	clock.elapsed = 0
	var internal_system = System.new(project, 0, null, "", func(): return clock.elapsed)
	var first = internal_system.get_event("Test/Event").create_instance()
	var other = internal_system.get_event(second.id).create_instance()
	first.callback.connect(func(_owner, payload):
		if payload.type == "StartedCallback": other.start())
	first.start()
	clock.elapsed = 10
	internal_system.update(0.25)
	test.check(other.timeline_position == 0.25, "callback-internal starts ignore between-tick driver clock")
	_clocked_replay(test)

func _quantized_starts(test) -> void:
	for scenario in [[0.75, 0.0, 1.0], [1.25, 0.0, 2.0], [0.75, 0.5, 2.0]]:
		var project = _minimal(180)
		var music = project.events[0]
		music.isConductor = true
		music.tempo = [{"position": 0, "bpm": 60}]
		var stinger = _event(project, "Stinger")
		_instrument(stinger)
		stinger.macros = {"startQuantization": "Beat", "scheduleLookahead": scenario[1]}
		var clock = {"elapsed": 0.0}
		var system = System.new(project, 0, null, "", func(): return clock.elapsed)
		var conductor = system.get_event(music.id).create_instance()
		conductor.start()
		system.update(0)
		system.commands.clear()
		clock.elapsed = scenario[0]
		var cue = system.get_event(stinger.id).create_instance()
		cue.start()
		var first_tick = scenario[0] + 0.1
		system.update(first_tick)
		test.check(cue.state == "Starting" and _of(system, "VoicePlay").is_empty(), "quantized start waits for grid projected from mutation boundary")
		system.commands.clear()
		system.update(scenario[2] + 0.1 - first_tick)
		test.check(cue.state == "Playing" and _near(cue.timeline_position, 0.1, 1e-9), "quantized waiting retains clocked start across ticks")
		test.check(_near(_first(system, "VoicePlay").get("startSeconds", -1), 0.1, 1e-9), "quantized native onset contains only post-grid interval")
		conductor.stop("Immediate")
		test.check(system.conductor == null, "stopped conductor clears grid authority")
	var project = _minimal()
	project.events[0].macros.startQuantization = "Bar"
	var system = System.new(project)
	var instance = system.get_event("Test/Event").create_instance()
	instance.start()
	system.update(0)
	test.check(instance.state == "Playing", "quantized start without conductor starts immediately")

func _stealing_limits(test) -> void:
	for mode in ["Oldest", "Quietest", "Furthest", "None", "Virtualize"]:
		var project = _minimal()
		project.events[0].macros = {"maxInstances": 1, "stealing": mode}
		var system = System.new(project)
		var first = system.get_event("Test/Event").create_instance()
		var callbacks: Array = []
		first.callback.connect(func(_owner, payload): callbacks.append(payload.type))
		first.start()
		system.update(0)
		var second = system.get_event("Test/Event").create_instance()
		second.start()
		system.update(0.1)
		if mode == "None":
			test.check(first.state == "Playing" and second.state == "Stopped", "stealing None rejects over-cap start")
		elif mode == "Virtualize":
			test.check(first.state == "Playing" and second.is_virtual, "virtualize stealing preserves both timelines")
			first.stop("Immediate")
			system.commands.clear()
			system.update(0.1)
			test.check(not second.is_virtual and _of(system, "VoicePlay").size() == 1, "virtualized instance resumes when slot frees")
		else:
			test.check(first.state == "Stopped" and second.state == "Playing" and callbacks.has("StolenCallback"), mode + " stealing emits callback and cuts victim")
	var project = _minimal()
	project.events[0].macros.priority = 200
	var important = _event(project, "Important")
	important.macros.priority = 10
	_instrument(important)
	var system = System.new(project)
	system.max_real_voices = 1
	var low = system.get_event("Test/Event").create_instance()
	var high = system.get_event("Important").create_instance()
	low.start()
	high.start()
	system.update(0)
	test.check(low.is_virtual and not high.is_virtual and system.real_voice_count == 1, "global voice cap admits lower priority number")
	high.stop("Immediate")
	system.update(0.1)
	test.check(not low.is_virtual and system.real_voice_count == 1, "global virtual voice unvirtualizes")
	var capped_project = _minimal()
	capped_project.mixer.buses[0].maxVoices = 1
	var capped = System.new(capped_project)
	capped.get_event("Test/Event").create_instance().start()
	capped.get_event("Test/Event").create_instance().start()
	capped.update(0)
	test.check(capped.real_voice_count == 1, "per-bus cap enforced independently of global cap")
	var cooldown_project = _minimal()
	cooldown_project.events[0].macros.cooldown = 1
	var cooldown = System.new(cooldown_project)
	cooldown.get_event("Test/Event").create_instance().start()
	var blocked = cooldown.get_event("Test/Event").create_instance()
	blocked.start()
	test.check(blocked.state == "Stopped", "cooldown rejects immediate subsequent start")
	cooldown.update(1)
	blocked.start()
	test.check(blocked.state == "Starting", "cooldown permits start exactly at boundary")

func _playlists_scatter(test) -> void:
	for mode in ["Sequential", "Random", "Shuffle"]:
		var project = _project()
		var event = _event(project)
		var multi = _instrument(event, "Multi", 0, 0.1)
		multi.mode = mode
		multi.avoidRepeat = 2
		multi.entries = []
		for i in range(4):
			multi.entries.append({"asset": "res://%d.wav" % i, "weight": 0 if i == 3 else 1})
			project.assetLengths["res://%d.wav" % i] = 0.1
		_marker(event, "Loop", 0, {"length": 0.2})
		var system = System.new(project, 7)
		system.get_event(event.id).create_instance().start()
		var assets: Array = []
		for _i in range(30):
			system.update(0.05)
			for play in _of(system, "VoicePlay"):
				assets.append(play.asset)
			system.commands.clear()
		test.check(assets.size() >= 7 and not assets.has("res://3.wav"), mode + " playlist cycles and ignores zero weights")
		for i in range(assets.size()):
			if mode == "Sequential":
				test.check(assets[i] == "res://%d.wav" % (i % 3), "sequential playlist preserves order")
			elif mode == "Random" and i >= 2:
				test.check(assets[i] != assets[i - 1] and assets[i] != assets[i - 2], "weighted random avoids entire recent window")
			elif mode == "Shuffle" and i % 3 == 2:
				test.check(assets[i] != assets[i - 1] and assets[i] != assets[i - 2] and assets[i - 1] != assets[i - 2], "shuffle visits enabled entries once per cycle")
	var project = _project()
	var event = _event(project)
	event.macros = {"is3D": true, "persistent": true}
	var scatter = _instrument(event, "Scatterer")
	scatter.merge({"minInterval": 0.2, "maxInterval": 0.2, "minScatterDistance": 2, "maxScatterDistance": 4, "polyphony": 3, "entries": [{"asset": "res://bird.wav"}]}, true)
	project.assetLengths["res://bird.wav"] = 5
	var system = System.new(project, 3)
	var instance = system.get_event(event.id).create_instance()
	var center = Vector3(10, 1, -5)
	instance.set_3d_attributes({"position": center})
	instance.start()
	var plays: Array = []
	for _i in range(9):
		system.update(0.1)
		plays.append_array(_of(system, "VoicePlay"))
		system.commands.clear()
	test.check(plays.size() >= 4 and system.real_voice_count == 3, "scatterer repeated spawns honor polyphony")
	for play in plays:
		var distance = play.attributes.position.distance_to(center)
		test.check(play.spatial and distance >= 1.999 and distance <= 4.001 and play.attributes.position.y == center.y, "scatterer positions remain within authored horizontal annulus")

func _nested_commands_programmer(test) -> void:
	var project = _project()
	var child_event = _event(project, "Child")
	child_event.macros.volumeDb = -3
	_instrument(child_event)
	project.assetLengths["res://tone.wav"] = 10
	var parent_event = _event(project, "Parent")
	parent_event.macros.volumeDb = -6
	var nested = _instrument(parent_event, "Event")
	nested.event = child_event.id
	nested.volumeDb = -2
	var system = System.new(project)
	var parent = system.get_event("Parent").create_instance()
	parent.start()
	system.update(0)
	test.check(system.instance_count == 2 and system.instances.size() == 1, "nested instance counted but not top-level scheduled")
	test.check(_first(system, "VoicePlay").get("volumeDb", 999) == -11, "nested child inherits complete parent instrument gain")
	parent.set_paused(true)
	system.commands.clear()
	system.update(0.5)
	test.check(_first(system, "VoiceSetPaused").get("paused", false), "pause propagates to nested child")
	parent.stop("Immediate")
	test.check(system.instance_count == 1, "stopping parent destroys owned child")
	var action_event = _event(project, "Action")
	var command = _instrument(action_event, "Command", 0.5)
	command.targetEvent = child_event.id
	command.action = "StartEvent"
	var action_system = System.new(project)
	var action = action_system.get_event("Action").create_instance()
	action.start()
	action_system.update(0.6)
	action_system.update(0)
	test.check(action_system.instances.any(func(i): return i.description.path == "Child" and i.state == "Playing"), "command instrument starts referenced event")
	var table_project = _project()
	var dialogue = _event(table_project)
	var programmer = _instrument(dialogue, "Programmer")
	programmer.key = "greeting"
	table_project.audioTables = [{"entries": {"greeting": {"en": "res://en.wav", "fr": "res://fr.wav"}}}]
	var localized = System.new(table_project)
	localized.locale = "fr"
	var speech = localized.get_event(dialogue.id).create_instance()
	speech.set_programmer_sound("res://fallback.wav")
	var requests: Array = []
	speech.callback.connect(func(_owner, payload):
		if payload.type == "ProgrammerSoundCallback": requests.append(payload))
	speech.start()
	localized.update(0)
	test.check(_first(localized, "VoicePlay").get("asset", "") == "res://fr.wav" and requests.is_empty(), "audio table locale precedes callback and fallback")
	localized.stop_all("Immediate")
	localized.locale = "missing"
	localized.commands.clear()
	localized.get_event(dialogue.id).create_instance().start()
	localized.update(0)
	test.check(_first(localized, "VoicePlay").get("asset", "") == "res://en.wav", "missing locale falls back to first authored locale")
	programmer.key = "unknown"
	var callback_system = System.new(table_project)
	var supplied = callback_system.get_event(dialogue.id).create_instance()
	supplied.set_programmer_sound("res://fallback.wav")
	supplied.callback.connect(func(_owner, payload):
		if payload.type == "ProgrammerSoundCallback": payload.asset = "res://callback.wav")
	supplied.start()
	callback_system.update(0)
	test.check(_first(callback_system, "VoicePlay").get("asset", "") == "res://callback.wav", "mutable programmer callback wins over instance fallback")
	var fallback = callback_system.get_event(dialogue.id).create_instance()
	fallback.set_programmer_sound("res://fallback.wav")
	fallback.callback_mask = 0
	callback_system.commands.clear()
	fallback.start()
	callback_system.update(0)
	test.check(_first(callback_system, "VoicePlay").get("asset", "") == "res://fallback.wav", "programmer fallback works with callback mask disabled")

func _spatial_automation_modulation(test) -> void:
	var emitter = Spatial.attributes({"position": Vector3(10, 0, 0), "velocity": Vector3(2, 0, 0), "forward": Vector3.LEFT})
	var listener = Spatial.origin()
	test.check(Spatial.compute("Distance", emitter, listener, 1, 20) == 10, "distance builtin")
	test.check(_near(Spatial.compute("Direction", emitter, listener, 1, 20), 90), "Godot negative-Z forward yields positive rightward yaw")
	test.check(Spatial.compute("EventConeAngle", emitter, listener, 1, 20) == 0, "emitter cone faces listener")
	test.check(Spatial.compute("Speed", emitter, listener, 1, 20) == 2 and Spatial.compute("SpeedAbsolute", emitter, listener, 1, 20) == 2, "relative radial and absolute speed builtins")
	var project = _minimal()
	var event = project.events[0]
	event.macros = {"is3D": true, "rolloff": "InverseSquare", "doppler": true, "dopplerScale": 0.5}
	var distance = _parameter(event, "Distance", 100)
	distance.builtIn = "Distance"
	var rpm = _parameter(event, "RPM", 2000)
	rpm["default"] = 1000
	event.automation = [{"target": {"kind": "EventVolumeDb", "owner": event.id}, "parameter": distance.id, "curve": {"points": [{"x": 0, "y": 0}, {"x": 20, "y": -20}]}}]
	event.modulators = [{"$type": "Autopitch", "target": {"kind": "EventPitch", "owner": event.id}, "parameter": rpm.id, "referenceValue": 1000, "pitchAtMinimum": 0.5, "pitchAtMaximum": 2.0, "amount": 1.0}]
	var system = System.new(project)
	system.set_listener(1, {"position": Vector3(9, 0, 0)}, 0.1)
	var instance = system.get_event(event.id).create_instance()
	instance.set_3d_attributes(emitter)
	instance.start()
	system.update(0)
	var play = _first(system, "VoicePlay")
	test.check(instance.get_parameter("Distance") == 1 and play.get("volumeDb", 999) == -1, "nearest enabled listener drives builtin automation regardless of positive weight magnitude")
	test.check(play.get("rolloff", "") == "InverseSquare" and play.get("doppler", false) and play.get("dopplerScale", 0) == 0.5, "spatial macros forwarded without losing authored scale")
	test.check(play.get("pitch", 0) == 1, "autopitch unity at reference")
	instance.set_parameter("RPM", 2000, true)
	instance.set_3d_attributes({"position": Vector3(13, 0, 0)})
	system.commands.clear()
	system.update(0.5)
	test.check(_first(system, "VoiceSetPitch").get("pitch", 0) == 2 and instance.get_parameter_velocity("Distance") == 6, "autopitch follows parameter and builtin derivative measures closing speed")
	test.check(_of(system, "VoiceSet3D").size() == 1, "moving emitter emits native attributes")
	var envelope_project = _minimal()
	var enveloped = envelope_project.events[0]
	enveloped.modulators = [{"$type": "Ahdsr", "amount": 80, "target": {"kind": "EventVolumeDb", "owner": enveloped.id}, "envelope": {"attack": 1, "sustain": 1, "release": 1}}]
	var envelope_system = System.new(envelope_project)
	var envelope_instance = envelope_system.get_event(enveloped.id).create_instance()
	envelope_instance.start()
	envelope_system.update(0)
	test.check(_first(envelope_system, "VoicePlay").get("volumeDb", 999) == -80, "event AHDSR starts below authored gain")
	envelope_system.commands.clear()
	envelope_system.update(0.5)
	test.check(_first(envelope_system, "VoiceSetVolume").get("volumeDb", 999) == -40, "AHDSR attack contributes modulation depth")
	envelope_instance.stop()
	envelope_system.update(0.5)
	test.check(envelope_instance.state == "Stopping", "AHDSR release extends stop fade even without authored fadeOut")
	envelope_system.update(0.5)
	test.check(envelope_instance.state == "Stopped", "AHDSR release completes instance lifecycle")
	var lfo_project = _minimal()
	var lfo_event = lfo_project.events[0]
	lfo_event.tempo = [{"position": 0, "bpm": 120}]
	lfo_event.modulators = [{"$type": "Lfo", "shape": "Sine", "rateHz": 1, "tempoSync": true, "amount": 6, "target": {"kind": "EventVolumeDb", "owner": lfo_event.id}}]
	var lfo_system = System.new(lfo_project)
	lfo_system.get_event(lfo_event.id).create_instance().start()
	lfo_system.update(0.125)
	test.check(_near(_first(lfo_system, "VoicePlay").get("volumeDb", 999), 6), "tempo-synced LFO converts beats to phase rate")

func _snapshots_delay_silence(test) -> void:
	var project = _minimal()
	var event = project.events[0]
	var clip = event.tracks[0].instruments[0]
	clip.triggerDelay = 0.2
	clip.startOffset = 0.5
	var system = System.new(project)
	var instance = system.get_event(event.id).create_instance()
	instance.start()
	system.update(0.1)
	test.check(_of(system, "VoicePlay").is_empty(), "trigger delay holds voice allocation")
	system.update(0.2)
	test.check(_near(_first(system, "VoicePlay").get("startSeconds", -1), 0.6), "late delayed trigger adds overshoot to authored asset offset")
	var silence_project = _project()
	var silent_event = _event(silence_project)
	_instrument(silent_event, "Silence", 0, 1)
	var silent = System.new(silence_project)
	var silent_instance = silent.get_event(silent_event.id).create_instance()
	silent_instance.start()
	silent.update(0.5)
	test.check(silent_instance.state == "Playing" and _of(silent, "VoicePlay").is_empty(), "silence region contributes duration without creating sound")
	silent.update(0.5)
	test.check(silent_instance.state == "Stopped", "silence event finishes at authored region end")
	var snapshot_project = _project()
	var snapshot = {"id": _id(), "name": "Duck", "intensity": {"attack": 0, "sustain": 1, "release": 0}, "values": []}
	snapshot_project.snapshots.append(snapshot)
	var snapshot_event = _event(snapshot_project)
	var instrument = _instrument(snapshot_event, "Snapshot", 0, 1)
	instrument.snapshot = snapshot.id
	var snapshot_system = System.new(snapshot_project)
	var snapshot_instance = snapshot_system.get_event(snapshot_event.id).create_instance()
	snapshot_instance.start()
	snapshot_system.update(0)
	test.check(snapshot_system.mixer.active_snapshot_count == 1, "snapshot instrument starts mixer snapshot")
	snapshot_system.update(1)
	test.check(snapshot_system.mixer.active_snapshot_count == 0, "snapshot region exit releases mixer snapshot")

func _determinism(test) -> void:
	var project = _project()
	var event = _event(project)
	var multi = _instrument(event, "Multi", 0, 0.1)
	multi.entries = [{"asset": "res://a.wav", "weight": 1}, {"asset": "res://b.wav", "weight": 2}, {"asset": "res://c.wav", "weight": 3}]
	multi.volumeRandomDb = 6
	multi.pitchRandom = 0.2
	project.assetLengths = {"res://a.wav": 0.1, "res://b.wav": 0.1, "res://c.wav": 0.1}
	_marker(event, "Loop", 0, {"length": 0.2})
	var logs: Array = []
	for seed in [7, 7, 8]:
		var system = System.new(project, seed)
		system.get_event(event.id).create_instance().start()
		var log: Array = []
		for _i in range(60):
			system.update(0.05)
			log.append_array(system.commands.duplicate(true))
			system.commands.clear()
		logs.append(log)
	test.check(logs[0].size() > 20 and logs[0] == logs[1] and logs[0] != logs[2], "seed controls exact repeatable complete command stream")

func _clocked_replay(test) -> void:
	var project = _minimal(180)
	var clock = {"elapsed": 0.0}
	var system = System.new(project, 0, null, "", func(): return clock.elapsed)
	var recorder = Recorder.new(system)
	system.update(0)
	system.commands.clear()
	clock.elapsed = 1.4
	var music = system.get_event("Test/Event").create_instance()
	music.start()
	music.timeline_position = 10
	system.update(1.425)
	system.commands.clear()
	clock.elapsed = 0.5
	music.timeline_position = 30
	system.update(0.525)
	system.commands.clear()
	music.stop("Immediate")
	music.release()
	system.update(0)
	var recording = recorder.stop()
	var loaded = RecordingJson.deserialize(RecordingJson.serialize(recording))
	var offsets: Array = []
	for tick in loaded.ticks:
		for call in tick.calls:
			if call.method == "SetUpdateOffset":
				offsets.append(call.value)
	test.check(offsets == [1.4, 1.4, 0.5], "recording stores start and latest seek mutation boundaries explicitly")
	test.check(Replayer.compare(recording, Replayer.replay(loaded, project)) == null, "serialized mutation offsets replay exact native command stream without live clock")

func _command_edges_and_ownership(test) -> void:
	var project = _minimal()
	var target = project.events[0]
	target.macros.persistent = true
	var commands = _event(project, "Commands")
	commands.macros.persistent = true
	var parameter = _parameter(commands, "Control")
	var set_command = _instrument(commands, "Command", 0.25)
	set_command.merge({"action": "SetParameter", "targetParameter": parameter.id, "value": 0.75}, true)
	var stop_command = _instrument(commands, "Command", 0.5)
	stop_command.merge({"action": "StopEvent", "targetEvent": target.id}, true)
	var system = System.new(project)
	var target_instance = system.get_event(target.id).create_instance()
	var control_instance = system.get_event(commands.id).create_instance()
	target_instance.start()
	control_instance.start()
	system.update(0)
	system.update(0.3)
	system.update(0)
	test.check(control_instance.get_parameter("Control") == 0.75, "set-parameter command targets local parameter by ID")
	system.update(0.3)
	test.check(target_instance.state == "Stopped", "stop-event command stops all matching descriptions")
	var probability_project = _minimal()
	probability_project.events[0].tracks[0].instruments[0].probability = 0
	var probability_system = System.new(probability_project)
	probability_system.get_event("Test/Event").create_instance().start()
	probability_system.update(0.25)
	test.check(_of(probability_system, "VoicePlay").is_empty(), "zero-probability instrument never fires")
	var fading_project = _minimal()
	var event = fading_project.events[0]
	event.macros.persistent = true
	var sheet = _parameter(event)
	var clip = event.tracks[0].instruments[0]
	clip.placement = {"parameter": sheet.id, "start": 0.5, "length": 0.5}
	clip.cutMode = "FadeOut"
	clip.fadeOut = 1.0
	var fading_system = System.new(fading_project)
	var fading_instance = fading_system.get_event(event.id).create_instance()
	fading_instance.set_parameter("P", 1, true)
	fading_instance.start()
	fading_system.update(0)
	fading_instance.set_parameter("P", 0, true)
	fading_system.commands.clear()
	fading_system.update(0.5)
	test.check(_of(fading_system, "VoiceStop").is_empty() and _near(_first(fading_system, "VoiceSetVolume").get("volumeDb", 999), -6.0205999), "condition-region fade exit keeps native voice until ramp completes")
	fading_system.update(0.5)
	test.check(_of(fading_system, "VoiceStop").size() == 1, "condition-region fade exits after authored duration")
	var recursive_project = _project()
	var recursive_event = _event(recursive_project)
	recursive_event.macros.persistent = true
	var recursive_instrument = _instrument(recursive_event, "Event")
	recursive_instrument.event = recursive_event.id
	var recursive_system = System.new(recursive_project)
	var recursive_instance = recursive_system.get_event(recursive_event.id).create_instance()
	recursive_instance.start()
	recursive_system.update(0)
	test.check(recursive_system.instance_count == 9, "runtime depth limit contains invalid recursive data at root plus eight nested levels")
	recursive_instance.stop("Immediate")
	test.check(recursive_system.instance_count == 1, "recursive child teardown releases every nested instance")
	var owned = System.new(_minimal())
	var owned_instance = owned.get_event("Test/Event").create_instance()
	owned_instance.start()
	owned.update(0)
	var weak_system = weakref(owned)
	var weak_description = weakref(owned_instance.description)
	owned_instance = null
	owned = null
	test.check(weak_system.get_ref() == null and weak_description.get_ref() == null, "runtime ownership graph releases without RefCounted cycles on unload")

func _tempo_and_instrument_modulation(test) -> void:
	var project = _minimal(20)
	var event = project.events[0]
	event.tempo = [{"position": 0, "bpm": 120, "beatsPerBar": 4}, {"position": 3, "bpm": 60, "beatsPerBar": 3}]
	var first_marker = _marker(event, "Destination", 1)
	_marker(event, "Destination", 5)
	var system = System.new(project)
	var description = system.get_event(event.id)
	test.check(description.next_grid(0.6, "Bar", false) == 2, "bar quantization uses authored signature")
	test.check(description.next_grid(2.5, "Bar", false) == 3, "new tempo marker starts a fresh bar before previous segment grid")
	test.check(description.next_grid(0.5, "Marker", false) == first_marker.position, "marker quantization resolves nearest destination")
	var beat = description.next_beat(3, true)
	test.check(beat.bar == 1 and beat.beat == 1 and beat.bpm == 60, "tempo changes reset bar count and BPM")
	var instrument = event.tracks[0].instruments[0]
	var parameter = _parameter(event, "Pitch", 2)
	parameter["default"] = 1
	event.automation = [
		{"target": {"kind": "InstrumentVolumeDb", "owner": instrument.id}, "parameter": parameter.id, "curve": {"points": [{"x": 0, "y": -10}, {"x": 2, "y": 0}]}},
		{"target": {"kind": "InstrumentPitch", "owner": instrument.id}, "parameter": parameter.id, "curve": {"points": [{"x": 0, "y": 0.5}, {"x": 2, "y": 1.5}]}}
	]
	instrument.modulators = [{"$type": "Autopitch", "target": {"kind": "InstrumentPitch", "owner": instrument.id}, "parameter": parameter.id, "referenceValue": 1, "amount": 0.5, "pitchAtMinimum": 0.5, "pitchAtMaximum": 2}]
	var instrument_system = System.new(project)
	var instance = instrument_system.get_event(event.id).create_instance()
	instance.set_parameter("Pitch", 2, true)
	instance.start()
	instrument_system.update(0)
	test.check(_near(_first(instrument_system, "VoicePlay").get("pitch", 0), 2.25), "instrument automation multiplies amount-scaled autopitch on first emitted voice")
	instance.set_parameter("Pitch", 0, true)
	instrument_system.commands.clear()
	instrument_system.update(0)
	test.check(_first(instrument_system, "VoiceSetVolume").get("volumeDb", 999) == -10 and _near(_first(instrument_system, "VoiceSetPitch").get("pitch", 0), 0.375), "instrument gain and pitch automation update existing voice")
