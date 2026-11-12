@tool
extends RefCounted

const Recorder = preload("recorder.gd")
const Recording = preload("recording.gd")

static func replay(recording: Dictionary, project: Dictionary, seed = null, assets = null) -> Dictionary:
	# Loaded at runtime to avoid a runtime -> profiling -> runtime preload cycle.
	var system = load("res://addons/dirge/model/Runtime/dirge_system.gd").new(project, int(recording.get("seed", 0)) if seed == null else int(seed), assets, recording.get("platform") if recording.get("platform") != null else "")
	system.locale = recording.get("locale", "")
	var recorder = Recorder.new(system)
	var session = Session.new(system)
	for tick in recording.get("ticks", []):
		for answer in tick.get("programmerSounds", []):
			session.queue_answer(answer)
		for call in tick.get("calls", []):
			session.apply(call)
		system.update(tick.get("dt", 0.0))
		system.commands.clear()
	return recorder.stop()

static func compare(expected: Dictionary, actual: Dictionary):
	var expected_ticks: Array = expected.get("ticks", [])
	var actual_ticks: Array = actual.get("ticks", [])
	var count: int = mini(expected_ticks.size(), actual_ticks.size())
	for t in count:
		var e: Array = expected_ticks[t].get("commands", [])
		var a: Array = actual_ticks[t].get("commands", [])
		var n: int = mini(e.size(), a.size())
		for i in n:
			if e[i] != a[i]:
				return {"tick": t, "index": i, "expected": e[i], "actual": a[i]}
		if e.size() != a.size():
			return {"tick": t, "index": n, "expected": e[n] if n < e.size() else "<no command>", "actual": a[n] if n < a.size() else "<no command>"}
	if expected_ticks.size() != actual_ticks.size():
		return {"tick": count, "index": 0, "expected": "<tick>" if expected_ticks.size() > count else "<end of recording>", "actual": "<tick>" if actual_ticks.size() > count else "<end of recording>"}
	return null

class Session extends RefCounted:
	var system
	var instances: Dictionary = {}
	var snapshots: Dictionary = {}
	var answers: Dictionary = {}

	func _init(owner) -> void:
		system = owner

	func queue_answer(record: Dictionary) -> void:
		var handle: int = int(record.get("handle", -1))
		if not answers.has(handle):
			answers[handle] = []
		answers[handle].append(record.get("asset", ""))

	func apply(source: Dictionary) -> void:
		var call: Dictionary = Recording.api_call()
		call.merge(source, true)
		match call.target:
			"System":
				_apply_system(call)
			"Instance":
				_apply_instance(call)
			"Mixer":
				_apply_mixer(call)
			"Snapshot":
				if call.method == "StartSnapshot":
					_apply_mixer(call)
				elif snapshots.has(int(call.handle)):
					var snapshot = snapshots[int(call.handle)]
					match call.method:
						"SetIntensity": snapshot.intensity = call.value
						"StopSnapshot": snapshot.stop()

	func _apply_system(call: Dictionary) -> void:
		var name: String = call.name if call.name != null else ""
		match call.method:
			"SetGlobalParameter": system.set_global_parameter(name, call.value, call.flag)
			"SetListener": system.set_listener(int(call.mode), Recording.unpack(call.vector), call.value)
			"StopAll": system.stop_all("Immediate" if int(call.mode) == 1 else "AllowFadeout")
			"SetLocale": system.locale = name
			"SetMaxRealVoices": system.max_real_voices = int(call.value)
			"Feedback":
				if name in ["VoiceFinished", "VoiceFailed"]:
					var feedback: Dictionary = {"type": name, "voice": int(call.value)}
					if name == "VoiceFailed":
						feedback.reason = call.text if call.text != null else ""
					system.feedback(feedback)

	func _apply_instance(call: Dictionary) -> void:
		var name: String = call.name if call.name != null else ""
		if call.method == "CreateInstance":
			var description = system.get_event(name)
			if description == null:
				return
			var instance = description.create_instance()
			var handle: int = int(call.result)
			instances[handle] = instance
			var session_ref: WeakRef = weakref(self)
			instance.callback.connect(func(source, event):
				var session = session_ref.get_ref()
				if session != null:
					session._answer_programmer(source, event, handle))
			return
		if not instances.has(int(call.handle)):
			return
		var instance = instances[int(call.handle)]
		match call.method:
			"Start": instance.start()
			"Stop": instance.stop("Immediate" if int(call.mode) == 1 else "AllowFadeout")
			"KeyOff": instance.key_off()
			"SetPaused": instance.set_paused(call.flag)
			"SetParameter": instance.set_parameter(name, call.value, call.flag)
			"SetParameterByLabel": instance.set_parameter_by_label(name, call.text if call.text != null else "")
			"Set3DAttributes": instance.set_3d_attributes(Recording.unpack(call.vector))
			"SetVolume": instance.set_volume(call.value)
			"SetPitch": instance.set_pitch(call.value)
			"SetProgrammerSound": instance.set_programmer_sound(name)
			"Release": instance.release()
			"Seek": instance.timeline_position = call.value
			"SetUpdateOffset": instance.set_update_offset(call.value)

	func _answer_programmer(_instance, event: Dictionary, handle: int) -> void:
		if event.get("type") == "ProgrammerSoundCallback" and answers.has(handle) and not answers[handle].is_empty():
			event.asset = answers[handle].pop_front()

	func _apply_mixer(call: Dictionary) -> void:
		var mixer = system.mixer
		var name: String = call.name if call.name != null else ""
		match call.method:
			"SetBusVolume":
				if not mixer.try_get_bus(name).is_empty(): mixer.set_bus_volume(name, call.value)
			"SetBusMute":
				if not mixer.try_get_bus(name).is_empty(): mixer.set_bus_mute(name, call.flag)
			"SetBusSolo":
				if not mixer.try_get_bus(name).is_empty(): mixer.set_bus_solo(name, call.flag)
			"SetVcaVolume":
				if not mixer.try_get_vca(name).is_empty(): mixer.set_vca_volume(name, call.value)
			"StartSnapshot":
				if mixer.has_snapshot(name): snapshots[int(call.result)] = mixer.start_snapshot(name)
