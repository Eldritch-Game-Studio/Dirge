@tool
extends RefCounted
const AutopitchMath = preload("autopitch_math.gd")

## The signed integer stores the original generator's unsigned 64-bit bit pattern.
class DirgeRandom:
	extends RefCounted
	const MULTIPLIER = 0x2545F4914F6CDD1D
	var _state: int
	func _init(seed: int = 0) -> void:
		_state = ((-7046029254386353131 if seed == 0 else (seed & 0xffffffff)) * MULTIPLIER) | 1
	func next_u_long() -> int:
		var x = _state
		x ^= (x >> 12) & 0x000fffffffffffff
		x ^= x << 25
		x ^= (x >> 27) & 0x0000001fffffffff
		_state = x
		return x * MULTIPLIER
	func next_float() -> float:
		return float((next_u_long() >> 40) & 0xffffff) / 16777216.0
	func next_signed() -> float:
		return next_float() * 2.0 - 1.0
	func next_int(max_exclusive: int) -> int:
		if max_exclusive <= 0: return 0
		var bits = next_u_long()
		var result = (bits & 0x7fffffffffffffff) % max_exclusive
		if bits < 0: result = (result + (0x7fffffffffffffff % max_exclusive + 1) % max_exclusive) % max_exclusive
		return result
	func chance(probability: float) -> bool:
		return probability >= 1.0 or (probability > 0.0 and next_float() < probability)
	func randf() -> float:
		return next_float()
	func randf_range(from: float, to: float) -> float:
		return from + (to - from) * next_float()
	func randi_range(from: int, to: int) -> int:
		return from + next_int(to - from + 1)

class AhdsrState:
	extends RefCounted
	var _env: Dictionary
	var _time: float = 0.0
	var is_released: bool = false
	var _release_from: float = 0.0
	var _release_start: float = 0.0
	var is_finished: bool:
		get: return is_released and _time - _release_start >= _env.get("release", 0.0)
	var current: float:
		get:
			if is_released:
				var duration: float = _env.get("release", 0.0)
				if duration <= 0.0: return 0.0
				var t = (_time - _release_start) / duration
				return 0.0 if t >= 1.0 else _release_from * (1.0 - t)
			var t = _time
			var attack: float = _env.get("attack", 0.0)
			if t < attack: return 1.0 if attack <= 0.0 else t / attack
			t -= attack
			var hold: float = _env.get("hold", 0.0)
			if t < hold: return 1.0
			t -= hold
			var decay: float = _env.get("decay", 0.0)
			var sustain: float = _env.get("sustain", 1.0)
			if t < decay: return 1.0 + (sustain - 1.0) * (t / decay)
			return sustain
	func _init(envelope: Dictionary) -> void:
		_env = envelope
	func advance(dt: float) -> float:
		_time += dt
		return current
	func release() -> void:
		if is_released: return
		_release_from = current
		is_released = true
		_release_start = _time

class LfoState:
	extends RefCounted
	var _lfo: Dictionary
	var _phase: float
	var _noise: float
	var _random: Variant
	var current: float:
		get:
			match _lfo.get("shape", "Sine"):
				"Sine": return sin(_phase * TAU)
				"Square": return 1.0 if _phase < 0.5 else -1.0
				"Triangle": return _phase * 4.0 - 1.0 if _phase < 0.5 else 3.0 - _phase * 4.0
				"SawUp": return _phase * 2.0 - 1.0
				"SawDown": return 1.0 - _phase * 2.0
				"Noise": return _noise
			return 0.0
	func _init(lfo: Dictionary, random: Variant = null) -> void:
		_lfo = lfo
		_phase = lfo.get("phase", 0.0)
		_random = random
		_noise = _random.randf() * 2.0 - 1.0 if _random != null else 0.0
	func advance(dt: float, rate_hz: float) -> float:
		var previous = _phase
		_phase = fmod(_phase + dt * rate_hz, 1.0)
		if _phase < previous and _random != null: _noise = _random.randf() * 2.0 - 1.0
		return current
