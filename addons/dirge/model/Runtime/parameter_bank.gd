@tool
extends RefCounted

class ParameterState:
	extends RefCounted
	var definition: Dictionary
	var target: float
	var value: float
	var velocity: float = 0.0
	var _previous: float

	func _init(authored: Dictionary):
		definition = authored
		value = float(authored.get("default", 0.0))
		target = value
		_previous = value

	func set_value(next: float, immediate: bool = false) -> void:
		target = clampf(next, definition.get("minimum", 0.0), definition.get("maximum", 1.0))
		if definition.get("kind", "Continuous") != "Continuous":
			# System.MathF.Round uses nearest-even at halfway values.
			var lower = floor(target)
			if target - lower == 0.5:
				target = lower if int(lower) % 2 == 0 else lower + 1.0
			else:
				target = round(target)
		if immediate:
			value = target

	func advance(dt: float) -> bool:
		var changed = value != target
		if changed:
			var speed = definition.get("seekSpeedUp", 0.0) if target > value else definition.get("seekSpeedDown", 0.0)
			value = target if speed <= 0 else move_toward(value, target, speed * dt)
		velocity = (value - _previous) / dt if dt > 0 else 0.0
		_previous = value
		return changed

	func rebase() -> void:
		_previous = value
		velocity = 0.0

var all: Array = []
var _lookup: Dictionary = {}

func _init(definitions: Array = []):
	for definition in definitions:
		var state = ParameterState.new(definition)
		all.append(state)
		_lookup[definition.get("id", "")] = state
		_lookup[definition.get("name", "")] = state

func get_state(id_or_name: String):
	return _lookup.get(id_or_name)

func get_value(id_or_name: String) -> float:
	var state = get_state(id_or_name)
	return state.value if state != null else 0.0

func set_value(id_or_name: String, value: float, immediate: bool = false) -> void:
	var state = get_state(id_or_name)
	if state != null:
		state.set_value(value, immediate)

func advance(dt: float) -> bool:
	var changed = false
	for state in all:
		changed = state.advance(dt) or changed
	return changed
