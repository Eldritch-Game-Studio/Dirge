@tool
extends RefCounted

const NONE = "00000000000000000000000000000000"
var state
var lengths: Dictionary = {}

func _init(editor_state) -> void:
	state = editor_state

func clear() -> void:
	lengths.clear()

func asset_length(path: String) -> float:
	if path.is_empty(): return 0.0
	if lengths.has(path): return lengths[path]
	var seconds = float(state.project.get("assetLengths", {}).get(path, 0.0))
	if not state.project.get("assetLengths", {}).has(path) and ResourceLoader.exists(path):
		var stream = ResourceLoader.load(path)
		if stream is AudioStream: seconds = stream.get_length()
	lengths[path] = seconds
	return seconds

func natural_length(instrument: Dictionary, depth: int = 0) -> float:
	var length = 0.0
	match instrument.get("$type", ""):
		"Clip": length = asset_length(instrument.get("asset", ""))
		"Multi", "Scatterer":
			for entry in instrument.get("entries", []): length = maxf(length, asset_length(entry.get("asset", "")))
		"Event": length = event_length(state.find_event(instrument.get("event", NONE)), depth + 1)
		"Command": return 0.05
	return length if length > 0.0 else 1.0

func event_length(event: Dictionary, depth: int = 0) -> float:
	if event.is_empty() or depth > 8: return 0.0
	var length = 0.0
	for track in event.get("tracks", []):
		for instrument in track.get("instruments", []):
			var placement = instrument.get("placement", {})
			if placement.get("parameter", NONE) != NONE: continue
			var duration = float(placement.get("length", 0.0))
			if duration <= 0: duration = natural_length(instrument, depth)
			length = maxf(length, float(placement.get("start", 0.0)) + duration)
	for marker in event.get("markers", []):
		length = maxf(length, float(marker.get("position", 0.0)) + float(marker.get("length", 0.0)))
	return length
