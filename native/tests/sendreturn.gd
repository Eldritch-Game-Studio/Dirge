extends Node
## Headless verification of the Dirge native GDExtension (run from stock Godot):
##
##   godot --headless --audio-driver Dummy --path . res://native/tests/sendreturn.tscn
##
## Bus layout (index order matters: a return bus must sit BEFORE its senders):
##   0 Master — AudioEffectCapture counting the frames the mixer has rendered
##   1 Ret   — DirgeAudioEffectReturn
##   2 A     — DirgeAudioEffectSend(target_bus = "Ret"), volume -80 dB, looping sine
##   3 Conv  — DirgeAudioEffectConvolutionReverb(unit impulse, dry -80 dB, wet 0 dB), looping sine
##
## Each phase is judged once the mixer has rendered SETTLE seconds of audio since its change, never after wall-clock
## time a starved host can outrun; a phase still waiting after STAGE_TIMEOUT_MSEC fails.
## Phase 1: pre-fader send — Ret and Master hear the sine although A's own contribution is silent
##   (counted from when Ret's keep-alive player exists).
## Phase 2: post-fader send — A's fader at -80 dB silences the send.
## Phase 3: convolution with a unit impulse reproduces the input level.
## Phase 4: a unit impulse delayed by DELAY_FRAMES (hundreds of partitions) still reproduces the input level
##   once DELAY_FRAMES + SETTLE have been rendered.
## Prints "NATIVE_TEST PASS" and exits 0, or the failures and exits 1.

const SINE_HZ := 440.0
const AMPLITUDE := 0.5 # -6.02 dBFS
const SETTLE := 0.5
const DELAY_FRAMES := 100000
const STAGE_TIMEOUT_MSEC := 30000
const KEEP_ALIVE := "DirgeReturnKeepAlive_Ret"
const STAGES := ["pre-fader send", "post-fader send", "unit-impulse convolution", "delayed-impulse convolution"]

var _clock: AudioEffectCapture
var _send: AudioEffect
var _conv: AudioEffect
var _sine: AudioStreamWAV
var _player_a: AudioStreamPlayer
var _player_conv: AudioStreamPlayer
var _stage := 0
var _stage_msec := 0
var _stage_from := -1 # rendered frames when the current stage's change took effect; -1 until it has
var _failures: PackedStringArray = []


func _ready() -> void:
	for cls in ["DirgeAudioEffectSend", "DirgeAudioEffectReturn", "DirgeAudioEffectConvolutionReverb"]:
		if not ClassDB.class_exists(cls):
			_fail("class %s is not registered (extension not loaded?)" % cls)
	if not _failures.is_empty():
		_finish()
		return
	_setup_buses()
	_sine = _looping_sine()
	_player_a = _make_player("A")
	_player_a.play()
	_stage_msec = Time.get_ticks_msec()


func _process(_delta: float) -> void:
	if _stage >= STAGES.size():
		return
	var rendered := _rendered_frames()
	# Ret only reaches Master once its keep-alive player plays, so phase 1's audio counts from then.
	if _stage_from < 0 and get_tree().root.has_node(KEEP_ALIVE):
		_stage_from = rendered
	var needed := _frames(SETTLE) + (DELAY_FRAMES if _stage == 3 else 0)
	if _stage_from >= 0 and rendered - _stage_from >= needed:
		_advance()
	elif Time.get_ticks_msec() - _stage_msec >= STAGE_TIMEOUT_MSEC:
		var progress: String = "%d of %d frames rendered" % [rendered - _stage_from, needed] if _stage_from >= 0 else "return keep-alive player missing under the scene root"
		_fail("%s: timed out after %d ms (%s)" % [STAGES[_stage], STAGE_TIMEOUT_MSEC, progress])
		_stage = STAGES.size()
		_finish()


func _advance() -> void:
	match _stage:
		0:
			_check_pre_fader()
			_send.set("pre_fader", false)
		1:
			_check_post_fader()
			_player_conv = _make_player("Conv")
			_player_conv.play()
		2:
			_check_convolution("unit impulse")
			_conv.set("impulse", _unit_impulse(DELAY_FRAMES))
		3:
			_check_convolution("delayed impulse")
	_stage += 1
	_stage_msec = Time.get_ticks_msec()
	_stage_from = _rendered_frames()
	if _stage == STAGES.size():
		_finish()


func _setup_buses() -> void:
	AudioServer.set_bus_count(1)
	AudioServer.set_bus_volume_db(0, 0.0)
	_clock = AudioEffectCapture.new()
	AudioServer.add_bus_effect(0, _clock)

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


## Frames the mixer has rendered since the clock was installed. A capture processes every Master step, even
## silent ones, and counts the frames it drops once its buffer is full.
func _rendered_frames() -> int:
	return _clock.get_pushed_frames() + _clock.get_discarded_frames()


func _frames(seconds: float) -> int:
	return roundi(seconds * AudioServer.get_mix_rate())


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


## One second of the sine as a seamless forward loop (whole cycles at any integer mix rate). The mixer renders
## it by itself, so a main thread that falls behind cannot starve the source into silence.
func _looping_sine() -> AudioStreamWAV:
	var rate := int(AudioServer.get_mix_rate())
	var bytes := PackedByteArray()
	bytes.resize(2 * rate)
	for i in rate:
		bytes.encode_s16(2 * i, roundi(32767.0 * AMPLITUDE * sin(TAU * SINE_HZ * i / rate)))
	var wav := AudioStreamWAV.new()
	wav.format = AudioStreamWAV.FORMAT_16_BITS
	wav.mix_rate = rate
	wav.stereo = false
	wav.data = bytes
	wav.loop_mode = AudioStreamWAV.LOOP_FORWARD
	wav.loop_end = rate
	return wav


func _make_player(bus: String) -> AudioStreamPlayer:
	var player := AudioStreamPlayer.new()
	player.stream = _sine
	player.bus = bus
	add_child(player)
	return player


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
	if not get_tree().root.has_node(KEEP_ALIVE):
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
	if _failures.is_empty():
		print("NATIVE_TEST PASS")
		get_tree().quit(0)
	else:
		print("NATIVE_TEST FAIL (%d)" % _failures.size())
		get_tree().quit(1)
