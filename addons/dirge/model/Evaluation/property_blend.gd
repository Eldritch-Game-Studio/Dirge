@tool
extends RefCounted

static func apply(current: float, authored: float, value: float, intensity: float, mode: String) -> float:
	if intensity <= 0.0: return current
	var from = authored if mode == "Overriding" else current
	return from + (value - from) * minf(intensity, 1.0)

## Input contributions are highest priority first, as in the original mixer.
static func compose(authored: float, sorted_by_priority_descending: Array) -> float:
	var result = authored
	for i in range(sorted_by_priority_descending.size() - 1, -1, -1):
		var c: Dictionary = sorted_by_priority_descending[i]
		result = apply(result, authored, c.value, c.intensity, c.mode)
	return result
