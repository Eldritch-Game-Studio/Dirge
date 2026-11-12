@tool
extends RefCounted

const Recording = preload("recording.gd")

static func serialize(recording: Dictionary, indented: bool = true) -> String:
	return JSON.stringify(_without_nulls(recording), "  " if indented else "", false, true)

static func deserialize(text: String) -> Dictionary:
	var json = JSON.new()
	if json.parse(text) != OK or not json.data is Dictionary:
		push_error("Invalid recording JSON: " + json.get_error_message())
		return {}
	var source: Dictionary = json.data
	var result: Dictionary = Recording.create()
	result.merge(source, true)
	if not result.ticks is Array:
		push_error("Recording ticks must be an array")
		return {}
	var ticks: Array = []
	for raw_tick in result.ticks:
		if not raw_tick is Dictionary:
			push_error("Recording tick must be an object")
			return {}
		var tick: Dictionary = Recording.tick()
		tick.merge(raw_tick, true)
		var calls: Array = []
		for raw_call in tick.calls:
			var call: Dictionary = Recording.api_call()
			call.merge(raw_call, true)
			calls.append(call)
		tick.calls = calls
		ticks.append(tick)
	result.ticks = ticks
	return result

static func _without_nulls(value):
	if value is Dictionary:
		var result: Dictionary = {}
		for key in value:
			if value[key] != null:
				result[key] = _without_nulls(value[key])
		return result
	if value is Array:
		var result: Array = []
		for item in value:
			result.append(_without_nulls(item))
		return result
	return value
