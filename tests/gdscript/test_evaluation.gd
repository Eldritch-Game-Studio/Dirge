extends RefCounted
const CurveMath = preload("res://addons/dirge/model/Evaluation/curve_math.gd")
const Blend = preload("res://addons/dirge/model/Evaluation/property_blend.gd")
const Mod = preload("res://addons/dirge/model/Evaluation/modulators.gd")
const Pitch = preload("res://addons/dirge/model/Evaluation/autopitch_math.gd")
const Meter = preload("res://addons/dirge/model/Metering/loudness_meter.gd")

func run(test: Object) -> void:
	_curves(test)
	_random(test)
	_envelopes(test)
	_lfo(test)
	_pitch_blend(test)
	_loudness(test)

func _near(test: Object, actual: float, expected: float, message: String, tolerance: float = 0.00001) -> void:
	test.check(absf(actual - expected) <= tolerance, "%s: expected %s, got %s" % [message, expected, actual])

func _curve_check(test: Object, curve: Dictionary, x: float, expected: float, message: String) -> void:
	var named = {"points":[]}
	var mixed = {"points":[]}
	for i in curve.points.size():
		var point: Array = curve.points[i]
		var entry = {"x":point[0], "y":point[1]}
		if point.size() > 2: entry.shape = point[2]
		named.points.append(entry)
		mixed.points.append(entry if i % 2 == 0 else point.duplicate())
	for input in [curve, named, mixed]:
		var before: Dictionary = input.duplicate(true)
		_near(test, CurveMath.evaluate(input, x), expected, message)
		test.check(input == before, "Evaluation does not mutate compact or named points")

func _curves(test: Object) -> void:
	_near(test, CurveMath.evaluate({}, 0.5), 0, "Empty curve")
	for x in [-10, 0.5, 10]: _curve_check(test, {"points":[[0.5, 7]]}, x, 7, "Single point is constant")
	var curve = {"points":[[0, 0], [2, 10], [4, 0]]}
	for pair in [[-1, 0], [0.5, 2.5], [1, 5], [2, 10], [3, 5], [5, 0]]:
		_curve_check(test, curve, pair[0], pair[1], "Linear curve and clamping")
	curve = {"points":[[0, 0, "Smooth"], [1, 1]]}
	for pair in [[0.25, 0.15625], [0.5, 0.5], [0.75, 0.84375]]:
		_curve_check(test, curve, pair[0], pair[1], "Smoothstep")
	curve = {"points":[[0, 1, "Stepped"], [1, 5], [2, 9]]}
	for pair in [[0.01, 1], [0.99, 1], [1, 1], [1.5, 7]]:
		_curve_check(test, curve, pair[0], pair[1], "Step closed at segment end")
	curve = {"points":[[0, 0], [1, 10], [1, 20], [2, 30]]}
	_curve_check(test, curve, 1, 10, "First duplicate wins at X")
	_curve_check(test, curve, 1.5, 25, "Second duplicate drives next segment")
	_near(test, CurveMath.db_to_linear(-6.0206), 0.5, "Decibel conversion", 0.0001)
	_near(test, CurveMath.linear_to_db(CurveMath.db_to_linear(-12)), -12, "Decibel roundtrip")
	test.check(CurveMath.db_to_linear(-80) == 0 and CurveMath.db_to_linear(-100) == 0 and CurveMath.linear_to_db(0) == -80, "Decibel silence floor")

func _random(test: Object) -> void:
	for seed in [0, 42, -7]:
		var a = Mod.DirgeRandom.new(seed)
		var b = Mod.DirgeRandom.new(seed)
		var equal = true
		for i in 1000: equal = equal and a.next_u_long() == b.next_u_long()
		test.check(equal, "Deterministic xorshift seed %d" % seed)
	test.check(Mod.DirgeRandom.new(1).next_u_long() != Mod.DirgeRandom.new(2).next_u_long(), "Different random seeds differ")
	test.check(Mod.DirgeRandom.new(0).next_u_long() != 0, "Zero seed does not lock generator")
	var r = Mod.DirgeRandom.new(123)
	var valid = true
	var seen = {}
	for i in 10000:
		var f = r.next_float()
		var s = r.next_signed()
		var n = r.next_int(7)
		valid = valid and f >= 0 and f < 1 and s >= -1 and s <= 1 and n >= 0 and n < 7
		seen[n] = true
	test.check(valid and seen.size() == 7 and r.next_int(0) == 0 and r.next_int(-5) == 0, "Random ranges and every bucket")
	r = Mod.DirgeRandom.new(5)
	test.check(r.chance(1) and r.chance(1.5) and not r.chance(0) and not r.chance(-1), "Probability boundaries")
	test.check(r.next_u_long() == Mod.DirgeRandom.new(5).next_u_long(), "Boundary probabilities consume no randomness")
	r = Mod.DirgeRandom.new(77)
	var hits = 0
	for i in 10000:
		if r.chance(0.5): hits += 1
	test.check(hits >= 4600 and hits <= 5400, "Half probability distribution")

func _envelopes(test: Object) -> void:
	var env = {"attack":1.0, "hold":0.5, "decay":1.0, "sustain":0.5, "release":2.0}
	var s = Mod.AhdsrState.new(env)
	_near(test, s.current, 0, "Envelope attack starts at zero")
	for pair in [[0.5, 0.5], [0.5, 1], [0.25, 1], [0.25, 1], [0.5, 0.75], [0.5, 0.5], [10, 0.5]]:
		_near(test, s.advance(pair[0]), pair[1], "AHDSR stage")
	test.check(not s.is_released and not s.is_finished, "Sustain is indefinite")
	s.release()
	_near(test, s.current, 0.5, "Release starts from sustain")
	_near(test, s.advance(1), 0.25, "Release midpoint")
	s.release()
	_near(test, s.current, 0.25, "Repeated release is idempotent")
	_near(test, s.advance(1), 0, "Release completes")
	test.check(s.is_finished and s.is_released, "Finished envelope flags")
	s = Mod.AhdsrState.new(env)
	s.advance(0.3)
	s.release()
	_near(test, s.advance(1), 0.15, "Release starts from partial attack")
	s = Mod.AhdsrState.new({})
	test.check(s.current == 1, "Default envelope instant full sustain")
	s.release()
	test.check(s.current == 0 and s.is_finished, "Zero release finishes immediately")

func _lfo(test: Object) -> void:
	for row in [["Sine", 0, 0], ["Sine", 0.25, 1], ["Sine", 0.5, 0], ["Sine", 0.75, -1], ["Square", 0, 1], ["Square", 0.49, 1], ["Square", 0.5, -1], ["Square", 0.99, -1], ["Triangle", 0, -1], ["Triangle", 0.25, 0], ["Triangle", 0.5, 1], ["Triangle", 0.75, 0], ["SawUp", 0, -1], ["SawUp", 0.5, 0], ["SawUp", 0.75, 0.5], ["SawDown", 0, 1], ["SawDown", 0.5, 0], ["SawDown", 0.75, -0.5]]:
		_near(test, Mod.LfoState.new({"shape":row[0], "phase":row[1]}).current, row[2], "LFO %s phase %s" % [row[0], row[1]])
	var lfo = Mod.LfoState.new({"shape":"SawUp"})
	_near(test, lfo.advance(0.25, 1), -0.5, "LFO phase accumulation")
	_near(test, lfo.advance(0.25, 2), 0.5, "LFO effective rate")
	_near(test, lfo.advance(0.125, 2), -1, "LFO phase wrap")
	var a = Mod.LfoState.new({"shape":"Noise"}, Mod.DirgeRandom.new(3))
	var b = Mod.LfoState.new({"shape":"Noise"}, Mod.DirgeRandom.new(3))
	var first = a.current
	test.check(a.advance(0.5, 1) == first, "Noise holds within a cycle")
	a.advance(0.75, 1)
	b.advance(0.5, 1)
	test.check(a.current != first and b.advance(0.75, 1) == a.current and absf(a.current) <= 1, "Noise changes deterministically on wrapping")
	test.check(Mod.LfoState.new({"shape":"Noise"}).advance(1.5, 1) == 0, "Noise without RNG is zero")

func _pitch_blend(test: Object) -> void:
	var m = {"referenceValue":1000, "pitchAtMinimum":0.5, "pitchAtMaximum":2, "amount":1}
	for pair in [[0, 0.5], [1000, 1], [4000, 2], [500, 0.75], [1750, 1.25], [-50, 0.5], [9000, 2]]:
		_near(test, Pitch.ratio(m, 0, 4000, pair[0]), pair[1], "Autopitch piecewise ratio")
	m.amount = 0.5
	_near(test, Pitch.evaluate(m, 0, 4000, 0), 0.75, "Autopitch amount below reference")
	_near(test, Pitch.evaluate(m, 0, 4000, 4000), 1.5, "Autopitch amount above reference")
	m.amount = 0
	_near(test, Pitch.evaluate(m, 0, 4000, 4000), 1, "Autopitch zero amount")
	m.referenceValue = 0
	_near(test, Pitch.ratio(m, 0, 1, 0), 1, "Reference at endpoint collapses side")
	_near(test, Pitch.ratio(m, 0, 1, 0.5), 1.5, "Collapsed endpoint interpolates other side")
	_near(test, Blend.apply(-12, 0, -6, 0.5, "Blending"), -9, "Blend starts from current")
	_near(test, Blend.apply(-20, 0, -6, 0.5, "Overriding"), -3, "Override starts from authored")
	_near(test, Blend.apply(-20, 0, -6, 0, "Overriding"), -20, "Zero intensity preserves lower priority")
	_near(test, Blend.apply(0, 0, -6, 3, "Blending"), -6, "Intensity clamps to one")
	var contributions = [{"priority":5, "value":-6, "intensity":0.5, "mode":"Blending"}, {"priority":1, "value":-12, "intensity":1, "mode":"Blending"}]
	_near(test, Blend.compose(0, contributions), -9, "Highest priority composed last")
	contributions[0].mode = "Overriding"
	contributions[0].intensity = 1
	_near(test, Blend.compose(0, contributions), -6, "Override discards lower priority")

func _sine(rate: int, channels: int, seconds: float, frequency: float, dbfs: float, only_channel: int = -1) -> PackedFloat32Array:
	var frames = int(rate * seconds)
	var buffer = PackedFloat32Array()
	buffer.resize(frames * channels)
	var amplitude = pow(10.0, dbfs / 20.0)
	for frame in frames:
		var sample = amplitude * sin(TAU * frequency * frame / rate)
		for channel in channels:
			buffer[frame * channels + channel] = sample if only_channel < 0 or channel == only_channel else 0.0
	return buffer

func _push_chunks(meter: Object, data: PackedFloat32Array, chunk_frames: int = 512) -> void:
	var size: int = chunk_frames * meter.channels
	for start in range(0, data.size(), size): meter.push(data.slice(start, mini(start + size, data.size())))

func _loudness(test: Object) -> void:
	for rate in [48000, 44100]:
		var meter = Meter.new(rate, 2)
		_push_chunks(meter, _sine(rate, 2, 3.0, 1000, -23))
		_near(test, meter.integrated_lufs, -23, "Stereo BS1770 integrated %d" % rate, 0.1)
		_near(test, meter.momentary_lufs, -23, "Stereo BS1770 momentary", 0.1)
		_near(test, meter.short_term_lufs, -23, "Stereo BS1770 short term", 0.1)
		meter.reset()
		test.check(meter.integrated_lufs == -INF and meter.momentary_lufs == -INF and meter.short_term_lufs == -INF and meter.true_peak_dbtp == -INF, "Meter reset clears every history")
		meter.push(_sine(rate, 2, 0.2, 997, 0))
		test.check(meter.true_peak_dbtp >= -0.01 and meter.true_peak_dbtp <= 0.2, "Annex 2 full-scale sine true peak")
	var mono = Meter.new(48000, 1)
	mono.push(_sine(48000, 1, 0.6, 1000, 0))
	_near(test, mono.integrated_lufs, -3.01, "Mono full scale reference", 0.1)
	var levels = []
	for channel in [0, 4, 3]:
		var surround = Meter.new(48000, 6)
		surround.push(_sine(48000, 6, 0.6, 1000, -20, channel))
		levels.append(surround.integrated_lufs)
	_near(test, levels[1] - levels[0], 10.0 * log(1.41) / log(10.0), "Rear channel weight", 0.05)
	test.check(levels[2] == -INF, "LFE excluded from loudness")
	for row in [[40, -23 - 5.61 - 0.691], [8000, -23 + 4 - 0.691]]:
		var weighted = Meter.new(48000, 2)
		weighted.push(_sine(48000, 2, 1, row[0], -23))
		_near(test, weighted.integrated_lufs, row[1], "K-weighting frequency response", 0.15)
	var peak = Meter.new(48000, 1)
	var samples = PackedFloat32Array()
	samples.resize(4800)
	for i in samples.size(): samples[i] = sin(PI * 0.5 * i + PI * 0.25)
	peak.push(samples)
	test.check(peak.true_peak_dbtp > -0.6 and peak.true_peak_dbtp < 0.2, "True peak detects inter-sample overshoot, not -3 dB sample peak")
	var gated = Meter.new(48000, 2)
	_push_chunks(gated, _sine(48000, 2, 20, 1000, -23), 1024)
	var silence = PackedFloat32Array()
	silence.resize(48000 * 2 * 10)
	_push_chunks(gated, silence, 1024)
	_push_chunks(gated, _sine(48000, 2, 10, 1000, -45), 1024)
	_near(test, gated.integrated_lufs, -23, "Absolute and relative gates reject silence and quiet passages", 0.1)
	_near(test, gated.momentary_lufs, -45, "Momentary follows quiet tail", 0.2)
	var silent = Meter.new(48000, 2)
	silent.push(silence.slice(0, 48000))
	test.check(silent.integrated_lufs == -INF and silent.momentary_lufs == -INF and silent.true_peak_dbtp == -INF, "Digital silence remains negative infinity")
	var partial = Meter.new(48000, 2)
	partial.push(PackedFloat32Array([1]))
	test.check(partial.true_peak_dbtp == -INF, "Trailing partial interleaved frame ignored")
