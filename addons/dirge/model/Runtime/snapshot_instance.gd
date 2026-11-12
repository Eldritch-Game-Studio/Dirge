@tool
extends RefCounted

const Modulators = preload("../Evaluation/modulators.gd")

var snapshot: Dictionary = {}
var resolved_values: Array = []
var envelope
var is_active: bool = false
var _mixer_ref: WeakRef
var _mixer:
	get:
		return _mixer_ref.get_ref() if _mixer_ref != null else null
	set(value):
		_mixer_ref = weakref(value) if value != null else null
var _intensity: float = 1.0
var intensity: float:
	get:
		return _intensity
	set(value):
		if _mixer != null:
			_mixer.record_call(self, "SetIntensity", null, null, value)
		_intensity = clampf(value, 0.0, 1.0)
var effective_intensity: float:
	get:
		return _intensity * envelope.current if is_active else 0.0

func activate(definition: Dictionary, mixer) -> void:
	snapshot = definition.snapshot
	resolved_values = definition.values
	_mixer = mixer
	envelope = Modulators.AhdsrState.new(snapshot.get("intensity", {}))
	_intensity = 1.0
	is_active = true

func deactivate() -> void:
	is_active = false

func stop() -> void:
	if _mixer != null:
		_mixer.record_call(self, "StopSnapshot")
	if is_active:
		envelope.release()
