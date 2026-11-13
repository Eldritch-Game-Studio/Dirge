@tool
extends RefCounted

const Curves = preload("../Evaluation/curve_math.gd")
const Modulators = preload("../Evaluation/modulators.gd")

static func is_none(id) -> bool:
	return id == null or str(id).is_empty() or str(id) == "00000000000000000000000000000000"

static func collect(source: Array, volume_kind: String, pitch_kind: String, owner: String) -> Array:
	var result: Array = []
	for mod in source:
		var target = mod.get("target", {})
		var kind = target.get("kind", "")
		if kind != volume_kind and kind != pitch_kind:
			continue
		if not is_none(target.get("owner")) and target.owner != owner:
			continue
		if mod.get("$type", "") == "Autopitch" and kind != pitch_kind:
			continue
		if mod.get("$type", "") in ["Random", "Ahdsr", "Lfo", "Autopitch"]:
			result.append({"definition": mod, "volume": kind == volume_kind})
	return result

static func lanes(source: Array, kind: String, owner: String) -> Array:
	var result: Array = []
	for lane in source:
		var target = lane.get("target", {})
		if target.get("kind", "") == kind and (is_none(target.get("owner")) or target.owner == owner):
			result.append(lane)
	return result

static func initialize(infos: Array, instance, random) -> Array:
	var runtime: Array = []
	for info in infos:
		var mod = info.definition
		match mod.get("$type", ""):
			"Random": runtime.append(random.next_signed())
			"Ahdsr": runtime.append(Modulators.AhdsrState.new(mod.get("envelope", {})))
			"Lfo": runtime.append(Modulators.LfoState.new(mod, random))
			"Autopitch": runtime.append(instance.resolve_parameter(mod.get("parameter", "")))
	return runtime

static func advance(infos: Array, runtime: Array, dt: float, bpm: float) -> Vector2:
	var volume = 0.0
	var pitch = 1.0
	for i in range(mini(infos.size(), runtime.size())):
		var info = infos[i]
		var mod = info.definition
		var state = runtime[i]
		var contribution = 0.0
		var amount = mod.get("amount", 0.0)
		match mod.get("$type", ""):
			"Random": contribution = amount * state
			"Ahdsr": contribution = amount * ((state.advance(dt) if dt > 0 else state.current) - 1.0)
			"Lfo":
				var rate = mod.get("rateHz", 1.0)
				if mod.get("tempoSync", false) and bpm > 0:
					rate = bpm / 60.0 / rate if rate > 0 else 0.0
				contribution = amount * (state.advance(dt, rate) if dt > 0 else state.current)
			"Autopitch":
				if state != null:
					contribution = amount * (Modulators.AutopitchMath.ratio(mod, state.definition.get("minimum", 0), state.definition.get("maximum", 1), state.value) - 1.0)
		if info.volume:
			volume += contribution
		else:
			pitch *= maxf(0.01, 1.0 + contribution)
	return Vector2(volume, pitch)

static func release(infos: Array, runtime: Array) -> void:
	for i in range(mini(infos.size(), runtime.size())):
		if infos[i].definition.get("$type", "") == "Ahdsr":
			runtime[i].release()

static func max_release(infos: Array) -> float:
	var result = 0.0
	for info in infos:
		if info.volume and info.definition.get("$type", "") == "Ahdsr":
			result = maxf(result, info.definition.get("envelope", {}).get("release", 0))
	return result

static func evaluate_lanes(source: Array, instance, product: bool = false) -> float:
	var result = 1.0 if product else 0.0
	for lane in source:
		var state = instance.resolve_parameter(lane.get("parameter", ""))
		if state != null:
			var value = Curves.evaluate(lane.get("curve", {}), state.value)
			result = result * maxf(0.01, value) if product else result + value
	return result
