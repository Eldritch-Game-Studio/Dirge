@tool
extends RefCounted

const Meter = preload("res://addons/dirge/model/Metering/loudness_meter.gd")
const MAX_FRAMES_PER_POLL = 65536
var effect = AudioEffectCapture.new()
var meter
var _sample_rate: int
var _interleaved = PackedFloat32Array()
var momentary_lufs: float:
	get: return meter.momentary_lufs
var short_term_lufs: float:
	get: return meter.short_term_lufs
var integrated_lufs: float:
	get: return meter.integrated_lufs
var true_peak_dbtp: float:
	get: return meter.true_peak_dbtp
var is_installed: bool:
	get: return _index_on_master() >= 0

func _init() -> void:
	effect.buffer_length = 0.5
	_sample_rate = roundi(AudioServer.get_mix_rate())
	meter = Meter.new(_sample_rate, 2)

func install() -> void:
	if is_installed:
		return
	var rate = roundi(AudioServer.get_mix_rate())
	if rate != _sample_rate:
		_sample_rate = rate
		meter = Meter.new(rate, 2)
	AudioServer.add_bus_effect(0, effect)
	effect.clear_buffer()

func uninstall() -> void:
	var index = _index_on_master()
	if index >= 0:
		AudioServer.remove_bus_effect(0, index)

func reset() -> void:
	meter.reset()
	effect.clear_buffer()

func dispose() -> void:
	uninstall()

func poll() -> void:
	var frames = mini(effect.get_frames_available(), MAX_FRAMES_PER_POLL)
	if frames <= 0:
		return
	var stereo = effect.get_buffer(frames)
	_interleaved.resize(stereo.size() * 2)
	for index in range(stereo.size()):
		_interleaved[2 * index] = stereo[index].x
		_interleaved[2 * index + 1] = stereo[index].y
	meter.push(_interleaved)

func _index_on_master() -> int:
	for index in range(AudioServer.get_bus_effect_count(0)):
		if AudioServer.get_bus_effect(0, index) == effect:
			return index
	return -1
