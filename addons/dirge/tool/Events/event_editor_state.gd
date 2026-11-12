@tool
extends RefCounted

signal selection_changed
const NONE = "00000000000000000000000000000000"
var document
var event_id: String = NONE
var sheet: String = NONE
var selected_instruments: Array = []
var selected_markers: Array = []
var selected_track: String = NONE
var selected_lane: String = NONE
var snap_to_grid: bool = true
var cursor: float = 0.0
var revision: int = 0
var project: Dictionary:
	get: return document.project if document != null else {}
var event: Dictionary:
	get: return find_event(event_id)
var primary_instrument: Dictionary:
	get: return find_instrument(selected_instruments[0]) if selected_instruments.size() == 1 else {}
var primary_marker: Dictionary:
	get: return find_marker(selected_markers[0]) if selected_markers.size() == 1 else {}

func _init(doc = null) -> void:
	document = doc

func bump() -> void:
	revision += 1

func notify_selection() -> void:
	bump()
	selection_changed.emit()

func set_event(id: String) -> void:
	event_id = id if not find_event(id).is_empty() else NONE
	selected_instruments.clear()
	selected_markers.clear()
	selected_track = NONE
	selected_lane = NONE
	sheet = NONE
	cursor = 0.0
	notify_selection()

func refresh() -> void:
	if event.is_empty():
		set_event(NONE)
		return
	selected_instruments = selected_instruments.filter(func(id): return not find_instrument(id).is_empty())
	selected_markers = selected_markers.filter(func(id): return not find_marker(id).is_empty())
	if find_track(selected_track).is_empty(): selected_track = NONE
	if find_lane(selected_lane).is_empty(): selected_lane = NONE
	if find_parameter(sheet).is_empty(): sheet = NONE
	bump()

func set_sheet(id: String) -> void:
	if sheet == id: return
	sheet = id
	selected_instruments.clear()
	selected_markers.clear()
	cursor = float(find_parameter(id).get("default", 0.0))
	notify_selection()

func clear_selection() -> void:
	selected_instruments.clear()
	selected_markers.clear()
	selected_track = NONE
	notify_selection()

func select_instrument(id: String, additive: bool = false) -> void:
	_select(id, selected_instruments, additive)

func select_marker(id: String, additive: bool = false) -> void:
	_select(id, selected_markers, additive)

func _select(id: String, list: Array, additive: bool) -> void:
	var existed = id in list
	if not additive:
		selected_instruments.clear()
		selected_markers.clear()
		selected_track = NONE
	if additive and existed: list.erase(id)
	elif id not in list: list.append(id)
	notify_selection()

func select_instruments(ids: Array, additive: bool = false) -> void:
	if not additive:
		selected_instruments.clear()
		selected_markers.clear()
		selected_track = NONE
	for id in ids:
		if id not in selected_instruments: selected_instruments.append(id)
	notify_selection()

func select_track(id: String) -> void:
	selected_instruments.clear()
	selected_markers.clear()
	selected_track = id
	notify_selection()

func select_lane(id: String) -> void:
	selected_lane = id
	notify_selection()

static func by_id(items: Array, id: String) -> Dictionary:
	if id == NONE or id.is_empty(): return {}
	for item in items:
		if item.get("id", NONE) == id: return item
	return {}

func find_event(id: String) -> Dictionary:
	return by_id(project.get("events", []), id)

func find_track(id: String) -> Dictionary:
	return by_id(event.get("tracks", []), id)

func instrument_track(id: String) -> Dictionary:
	for track in event.get("tracks", []):
		if not by_id(track.get("instruments", []), id).is_empty(): return track
	return {}

func find_instrument(id: String) -> Dictionary:
	return by_id(instrument_track(id).get("instruments", []), id)

func find_marker(id: String) -> Dictionary:
	return by_id(event.get("markers", []), id)

func parameters() -> Array:
	return event.get("localParameters", []) + project.get("globalParameters", [])

func find_parameter(id: String) -> Dictionary:
	return by_id(parameters(), id)

func is_global_parameter(id: String) -> bool:
	return not by_id(project.get("globalParameters", []), id).is_empty()

func find_lane(id: String) -> Dictionary:
	return by_id(event.get("automation", []), id)

func find_bus(id: String) -> Dictionary:
	return by_id(project.get("mixer", {}).get("buses", []), id)

func find_snapshot(id: String) -> Dictionary:
	return by_id(project.get("snapshots", []), id)
