extends Node
## Headless verification of the Dirge native GDExtension (run from stock Godot):
##
##   godot --headless --audio-driver Dummy --path . res://native/tests/sendreturn.tscn
##
## Bus layout (index order matters: a return bus must sit BEFORE its senders):
##   0 Master
##   1 Ret   — DirgeAudioEffectReturn
##   2 A     — DirgeAudioEffectSend(target_bus = "Ret"), volume -80 dB, sine generator
##   3 Conv  — DirgeAudioEffectConvolutionReverb(unit impulse, dry -80 dB, wet 0 dB), sine generator
##
## Phase 1 (0.5 s): pre-fader send — Ret and Master hear the sine although A's own
##   contribution is silent.
## Phase 2 (1.0 s): post-fader send — A's fader at -80 dB silences the send.
## Phase 3 (1.5 s): convolution with a unit impulse reproduces the input level.
## Phase 4 (4.5 s): a unit impulse delayed by DELAY_FRAMES (hundreds of partitions)
##   still reproduces the input level once the delay has elapsed.
## Prints "NATIVE_TEST PASS" and exits 0, or the failures and exits 1.

const SINE_HZ := 440.0
const AMPLITUDE := 0.5 # -6.02 dBFS
const PHASE_1 := 0.5
const PHASE_2 := 1.0
const PHASE_3 := 1.5
const PHASE_4 := 4.5
const DELAY_FRAMES := 100000

var _send: AudioEffect
var _conv: AudioEffect
var _player_a: AudioStreamPlayer
var _player_conv: AudioStreamPlayer
var _phase_a := 0.0
var _phase_conv := 0.0
var _elapsed := 0.0
var _stage := 0
var _failures: PackedStringArray = []


func _ready() -> void:
	for cls in ["DirgeAudioEffectSend", "DirgeAudioEffectReturn", "DirgeAudioEffectConvolutionReverb"]:
		if not ClassDB.class_exists(cls):
			_fail("class %s is not registered (extension not loaded?)" % cls)
	if not _failures.is_empty():
		_finish()
		return
	_setup_buses()
	_player_a = _make_player("A")
	_player_a.play()


func _process(delta: float) -> void:
	_feed(_player_a, SINE_HZ, "_phase_a")
	if _player_conv != null:
		_feed(_player_conv, SINE_HZ, "_phase_conv")
	_elapsed += delta
	if _stage == 0 and _elapsed >= PHASE_1:
		_stage = 1
		_check_pre_fader()
		_send.set("pre_fader", false)
	elif _stage == 1 and _elapsed >= PHASE_2:
		_stage = 2
		_check_post_fader()
		_player_conv = _make_player("Conv")
		_player_conv.play()
	elif _stage == 2 and _elapsed >= PHASE_3:
		_stage = 3
		_check_convolution("unit impulse")
		_conv.set("impulse", _unit_impulse(DELAY_FRAMES))
	elif _stage == 3 and _elapsed >= PHASE_4:
		_stage = 4
		_check_convolution("delayed impulse")
		_finish()


func _setup_buses() -> void:
	AudioServer.set_bus_count(1)
	AudioServer.set_bus_volume_db(0, 0.0)

	AudioServer.add_bus(1)
	AudioServer.set_bus_name(1, "Ret")
	var ret: AudioEffect = ClassDB.instantiate("DirgeAudioEffectReturn")
	AudioServer.add_bus_effect(1, ret)

	AudioServer.add_bus(2)
	AudioServer.set_bus_name(2, "A")
	_send = ClassDB.instantiate("DirgeAudioEffectSend")
	_send.set("target_bus", "Ret")
	_send.set("level_db", 0.0)
	_send.set("pre_fader", true)
	AudioServer.add_bus_effect(2, _send)
	AudioServer.set_bus_volume_db(2, -80.0)

	AudioServer.add_bus(3)
	AudioServer.set_bus_name(3, "Conv")
	_conv = ClassDB.instantiate("DirgeAudioEffectConvolutionReverb")
	_conv.set("impulse", _unit_impulse(0))
	_conv.set("wet_db", 0.0)
	_conv.set("dry_db", -80.0)
	_conv.set("gain_db", 0.0)
	AudioServer.add_bus_effect(3, _conv)

func _unit_impulse(delay_frames: int) -> AudioStreamWAV:
	var wav := AudioStreamWAV.new()
	wav.format = AudioStreamWAV.FORMAT_16_BITS
	wav.mix_rate = int(AudioServer.get_mix_rate())
	wav.stereo = false
	var bytes := PackedByteArray()
	bytes.resize(2 * (delay_frames + 64))
	bytes.encode_s16(2 * delay_frames, 32767)
	wav.data = bytes
	return wav


func _make_player(bus: String) -> AudioStreamPlayer:
	var generator := AudioStreamGenerator.new()
	generator.mix_rate = AudioServer.get_mix_rate()
	generator.buffer_length = 0.1
	var player := AudioStreamPlayer.new()
	player.stream = generator
	player.bus = bus
	add_child(player)
	return player


func _feed(player: AudioStreamPlayer, hz: float, phase_property: String) -> void:
	var playback := player.get_stream_playback() as AudioStreamGeneratorPlayback
	if playback == null:
		return
	var phase: float = get(phase_property)
	var increment := hz / AudioServer.get_mix_rate()
	var frames := playback.get_frames_available()
	for _i in frames:
		var v := AMPLITUDE * sin(phase * TAU)
		playback.push_frame(Vector2(v, v))
		phase = fmod(phase + increment, 1.0)
	set(phase_property, phase)


func _peak(bus: String) -> float:
	return AudioServer.get_bus_peak_volume_left_db(AudioServer.get_bus_index(bus), 0)


func _check_pre_fader() -> void:
	var ret := _peak("Ret")
	var master := _peak("Master")
	var a := _peak("A")
	print("phase1 pre-fader: Ret %.1f dB, Master %.1f dB, A %.1f dB" % [ret, master, a])
	if ret <= -40.0:
		_fail("pre-fader: Ret peak %.1f dB <= -40 dB" % ret)
	if master <= -40.0:
		_fail("pre-fader: Master peak %.1f dB <= -40 dB" % master)
	if a >= -60.0:
		_fail("pre-fader: A should be silent at -80 dB, peak %.1f dB" % a)
	if not get_tree().root.has_node("DirgeReturnKeepAlive_Ret"):
		_fail("return keep-alive player missing under the scene root")


func _check_post_fader() -> void:
	var ret := _peak("Ret")
	print("phase2 post-fader: Ret %.1f dB" % ret)
	if ret >= -60.0:
		_fail("post-fader: A's fader at -80 dB should silence the send, Ret peak %.1f dB" % ret)


func _check_convolution(label: String) -> void:
	var conv := _peak("Conv")
	var expected := linear_to_db(AMPLITUDE)
	print("convolution (%s): Conv %.2f dB (expected %.2f dB)" % [label, conv, expected])
	if absf(conv - expected) > 0.5:
		_fail("convolution (%s): expected the input level, peak %.2f dB vs %.2f dB" % [label, conv, expected])


func _fail(message: String) -> void:
	_failures.append(message)
	push_error("NATIVE_TEST: " + message)


func _finish() -> void:
	set_process(false)
	var players: Array = [_player_a, _player_conv, get_tree().root.get_node_or_null("DirgeReturnKeepAlive_Ret")]
	for player in players:
		if is_instance_valid(player):
			player.stop()
			player.queue_free()
	AudioServer.set_bus_count(1)
	_send = null
	_conv = null
	# Consume stop requests before exit; stock 4.7.2 still retains generator playbacks (see qualification evidence).
	await get_tree().create_timer(0.05).timeout
	if _failures.is_empty():
		print("NATIVE_TEST PASS")
		get_tree().quit(0)
	else:
		print("NATIVE_TEST FAIL (%d)" % _failures.size())
		get_tree().quit(1)
