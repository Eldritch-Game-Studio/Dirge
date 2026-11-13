@tool
extends RefCounted

static func ratio(modulator: Dictionary, minimum: float, maximum: float, value: float) -> float:
	var reference = clampf(modulator.get("referenceValue", 0.0), minf(minimum, maximum), maxf(minimum, maximum))
	value = clampf(value, minf(minimum, maximum), maxf(minimum, maximum))
	if value == reference: return 1.0
	if value < reference:
		var span = reference - minimum
		var pitch: float = modulator.get("pitchAtMinimum", 0.5)
		return 1.0 if span <= 0.0 else pitch + (1.0 - pitch) * ((value - minimum) / span)
	var span = maximum - reference
	return 1.0 if span <= 0.0 else 1.0 + (modulator.get("pitchAtMaximum", 2.0) - 1.0) * ((value - reference) / span)

static func evaluate(modulator: Dictionary, minimum: float, maximum: float, value: float) -> float:
	return 1.0 + modulator.get("amount", 0.0) * (ratio(modulator, minimum, maximum, value) - 1.0)
