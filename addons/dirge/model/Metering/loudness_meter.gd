@tool
extends RefCounted
## ITU-R BS.1770-4 K-weighting, 400 ms/3 s windows, two-stage gating,
## and Annex 2 four-phase true-peak interpolation. Interleaved L R C LFE Ls Rs.
const FIR = [
	0.0017089843750, 0.0109863281250, -0.0196533203125, 0.0332031250000, -0.0594482421875, 0.1373291015625,
	0.9721679687500, -0.1022949218750, 0.0476074218750, -0.0266113281250, 0.0148925781250, -0.0083007812500,
	-0.0291748046875, 0.0292968750000, -0.0517578125000, 0.0891113281250, -0.1665039062500, 0.4650878906250,
	0.7797851562500, -0.2003173828125, 0.1015625000000, -0.0582275390625, 0.0330810546875, -0.0189208984375,
	-0.0189208984375, 0.0330810546875, -0.0582275390625, 0.1015625000000, -0.2003173828125, 0.7797851562500,
	0.4650878906250, -0.1665039062500, 0.0891113281250, -0.0517578125000, 0.0292968750000, -0.0291748046875,
	-0.0083007812500, 0.0148925781250, -0.0266113281250, 0.0476074218750, -0.1022949218750, 0.9721679687500,
	0.1373291015625, -0.0594482421875, 0.0332031250000, -0.0196533203125, 0.0109863281250, 0.0017089843750]
var sample_rate: int
var channels: int
var momentary_lufs: float = -INF
var short_term_lufs: float = -INF
var integrated_lufs: float:
	get:
		if _integrated_dirty:
			_integrated = _compute_integrated()
			_integrated_dirty = false
		return _integrated
var true_peak_dbtp: float:
	get: return 20.0 * log(_true_peak) / log(10.0) if _true_peak > 0.0 else -INF
var _weights: PackedFloat64Array
var _a: PackedFloat64Array
var _b: PackedFloat64Array
var _z1a: PackedFloat64Array
var _z2a: PackedFloat64Array
var _z1b: PackedFloat64Array
var _z2b: PackedFloat64Array
var _step_energy: PackedFloat64Array
var _step_samples: int
var _step_fill: int = 0
var _step_ring: PackedFloat64Array
var _ring_head: int = 0
var _ring_count: int = 0
var _block_power: Array[float] = []
var _integrated_dirty: bool = true
var _integrated: float = -INF
var _history: PackedFloat64Array
var _history_pos: int = 0
var _true_peak: float = 0.0

func _init(rate: int, channel_count: int) -> void:
	assert(rate >= 8000, "Sample rate must be at least 8 kHz.")
	assert(channel_count in [1, 2, 6], "Supported layouts: 1, 2 or 6 channels.")
	sample_rate = rate
	channels = channel_count
	_weights = PackedFloat64Array([1.0] if channels == 1 else ([1.0, 1.0] if channels == 2 else [1.0, 1.0, 1.0, 0.0, 1.41, 1.41]))
	var k = tan(PI * 1681.974450955533 / rate)
	var q = 0.7071752369554196
	var vh = pow(10.0, 3.999843853973347 / 20.0)
	var vb = pow(vh, 0.4996667741545416)
	var a0 = 1.0 + k / q + k * k
	_a = PackedFloat64Array([(vh + vb * k / q + k * k) / a0, 2.0 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0, 2.0 * (k * k - 1.0) / a0, (1.0 - k / q + k * k) / a0])
	k = tan(PI * 38.13547087602444 / rate)
	q = 0.5003270373238773
	a0 = 1.0 + k / q + k * k
	_b = PackedFloat64Array([1.0 / a0, -2.0 / a0, 1.0 / a0, 2.0 * (k * k - 1.0) / a0, (1.0 - k / q + k * k) / a0])
	_z1a.resize(channels)
	_z2a.resize(channels)
	_z1b.resize(channels)
	_z2b.resize(channels)
	_step_energy.resize(channels)
	_history.resize(12 * channels)
	_step_ring.resize(30)
	# Math.Round uses ties-to-even; integer rates ending in 5 can land on a half.
	var steps = rate / 10.0
	_step_samples = maxi(1, int(floor(steps)) if fmod(steps, 1.0) == 0.5 and int(floor(steps)) % 2 == 0 else int(round(steps)))
	reset()

func reset() -> void:
	_z1a.fill(0.0)
	_z2a.fill(0.0)
	_z1b.fill(0.0)
	_z2b.fill(0.0)
	_step_energy.fill(0.0)
	_step_ring.fill(0.0)
	_history.fill(0.0)
	_step_fill = 0
	_ring_head = 0
	_ring_count = 0
	_block_power.clear()
	_integrated_dirty = true
	_integrated = -INF
	_history_pos = 0
	_true_peak = 0.0
	momentary_lufs = -INF
	short_term_lufs = -INF

func push(interleaved: PackedFloat32Array) -> void:
	var frames = int(interleaved.size() / channels)
	for frame in frames:
		var base = frame * channels
		for channel in channels:
			var sample: float = interleaved[base + channel]
			_true_peak_sample(channel, sample)
			if _weights[channel] == 0.0: continue
			var y = _a[0] * sample + _z1a[channel]
			_z1a[channel] = _a[1] * sample - _a[3] * y + _z2a[channel]
			_z2a[channel] = _a[2] * sample - _a[4] * y
			var w = _b[0] * y + _z1b[channel]
			_z1b[channel] = _b[1] * y - _b[3] * w + _z2b[channel]
			_z2b[channel] = _b[2] * y - _b[4] * w
			_step_energy[channel] += w * w
		_step_fill += 1
		if _step_fill >= _step_samples: _complete_step()

func _true_peak_sample(channel: int, sample: float) -> void:
	var offset = channel * 12
	_history[offset + _history_pos] = sample
	for phase in 4:
		var acc = 0.0
		var index = _history_pos
		for tap in 12:
			acc += FIR[phase * 12 + tap] * _history[offset + index]
			index = 11 if index == 0 else index - 1
		_true_peak = maxf(_true_peak, absf(acc))
	if channel == channels - 1: _history_pos = (_history_pos + 1) % 12

func _complete_step() -> void:
	var weighted = 0.0
	for channel in channels:
		weighted += _weights[channel] * _step_energy[channel]
		_step_energy[channel] = 0.0
	_step_fill = 0
	_step_ring[_ring_head] = weighted
	_ring_head = (_ring_head + 1) % 30
	_ring_count = mini(_ring_count + 1, 30)
	if _ring_count >= 4:
		var block = _sum_last_steps(4) / (4.0 * _step_samples)
		momentary_lufs = _to_lufs(block)
		_block_power.append(block)
		_integrated_dirty = true
	if _ring_count >= 30: short_term_lufs = _to_lufs(_sum_last_steps(30) / (30.0 * _step_samples))

func _sum_last_steps(count: int) -> float:
	var total = 0.0
	var index = _ring_head
	for i in count:
		index = 29 if index == 0 else index - 1
		total += _step_ring[index]
	return total

func _compute_integrated() -> float:
	var absolute_gate = _from_lufs(-70.0)
	var total = 0.0
	var count = 0
	for block in _block_power:
		if block <= absolute_gate: continue
		total += block
		count += 1
	if count == 0: return -INF
	var threshold = maxf(absolute_gate, _from_lufs(_to_lufs(total / count) - 10.0))
	total = 0.0
	count = 0
	for block in _block_power:
		if block <= threshold: continue
		total += block
		count += 1
	return -INF if count == 0 else _to_lufs(total / count)

static func _to_lufs(power: float) -> float:
	return -0.691 + 10.0 * log(power) / log(10.0) if power > 0.0 else -INF

static func _from_lufs(lufs: float) -> float:
	return pow(10.0, (lufs + 0.691) / 10.0)
