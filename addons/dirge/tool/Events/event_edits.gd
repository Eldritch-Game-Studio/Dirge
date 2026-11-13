@tool
extends RefCounted

const Model = preload("res://addons/dirge/model/Project/project_model.gd")
const NONE = "00000000000000000000000000000000"
const INSTRUMENT_KINDS = ["Clip", "Multi", "Scatterer", "Silence", "Event", "Programmer", "Command", "Snapshot"]
const MARKER_KINDS = ["Loop", "Transition", "TransitionRegion", "SustainPoint", "Destination", "Magnet"]
var state

func _init(editor_state) -> void:
	state = editor_state

func set_value(label: String, object: Dictionary, key: String, value) -> void:
	if object.get(key) == value: return
	state.document.transact(label, func(): object[key] = value)

func list_add(label: String, list: Array, item) -> void:
	state.document.transact(label, func(): list.append(item))

func list_remove(label: String, list: Array, item) -> void:
	if item not in list: return
	state.document.transact(label, func(): list.erase(item))

func add_track(name: String = "") -> Dictionary:
	var event = state.event
	var track = Model.create("Track", {"name": name if not name.is_empty() else "Track %d" % (event.get("tracks", []).size() + 1)})
	list_add("Add track", event.tracks, track)
	return track

func remove_track(track: Dictionary) -> void:
	list_remove("Remove track", state.event.tracks, track)

func reorder_track(track: Dictionary, index: int) -> void:
	var tracks: Array = state.event.tracks
	if track not in tracks: return
	state.document.transact("Reorder tracks", func():
		tracks.erase(track)
		tracks.insert(clampi(index, 0, tracks.size()), track))

func add_instrument(track: Dictionary, instrument: Dictionary) -> void:
	list_add("Add " + instrument.get("$type", "instrument"), track.instruments, instrument)

func move_instrument(id: String, to: Dictionary) -> void:
	var from = state.instrument_track(id)
	var instrument = state.find_instrument(id)
	if from.is_empty() or from == to: return
	state.document.transact("Move instrument", func():
		from.instruments.erase(instrument)
		to.instruments.append(instrument))

func delete_selection() -> bool:
	var ids: Array = state.selected_instruments.duplicate()
	var markers: Array = state.selected_markers.duplicate()
	if ids.is_empty() and markers.is_empty(): return false
	var event = state.event
	state.document.transact("Delete selection", func():
		for track in event.tracks:
			for index in range(track.instruments.size() - 1, -1, -1):
				if track.instruments[index].id in ids: track.instruments.remove_at(index)
		for index in range(event.markers.size() - 1, -1, -1):
			if event.markers[index].id in markers: event.markers.remove_at(index))
	state.refresh()
	return true

# Pointer drags preview in the canvases, then commit one atomic undo operation on release.
func set_placements(changes: Array, target_track: String = NONE) -> void:
	if changes.is_empty(): return
	state.document.transact("Move / resize instruments", func():
		for change in changes:
			var instrument = state.find_instrument(change.id)
			if instrument.is_empty(): continue
			instrument.placement.start = change.start
			instrument.placement.length = change.length
		if changes.size() == 1 and target_track != NONE:
			var from = state.instrument_track(changes[0].id)
			var to = state.find_track(target_track)
			if not to.is_empty() and from != to:
				var instrument = state.find_instrument(changes[0].id)
				from.instruments.erase(instrument)
				to.instruments.append(instrument))

func set_marker_positions(changes: Array) -> void:
	if changes.is_empty(): return
	state.document.transact("Move / resize markers", func():
		for change in changes:
			var marker = state.find_marker(change.id)
			if marker.is_empty(): continue
			marker.position = change.position
			if is_region(marker): marker.length = change.length)

func add_marker(marker: Dictionary) -> void:
	list_add("Add marker", state.event.markers, marker)

func add_tempo(position: float) -> void:
	var tempo: Array = state.event.tempo
	var previous: Dictionary = tempo.back() if not tempo.is_empty() else {}
	var marker = Model.create("TempoMarker", {"position": position, "bpm": previous.get("bpm", 120.0), "beatsPerBar": previous.get("beatsPerBar", 4)})
	state.document.transact("Add tempo marker", func():
		tempo.append(marker)
		tempo.sort_custom(func(a, b): return a.position < b.position))

func set_tempo_position(marker: Dictionary, position: float) -> void:
	var tempo: Array = state.event.tempo
	if marker.get("position", 0.0) == position: return
	state.document.transact("Move tempo marker", func():
		marker.position = maxf(0.0, position)
		tempo.sort_custom(func(a, b): return a.position < b.position))

func set_curve_points(lane: Dictionary, points: Array) -> void:
	var fresh = points.duplicate(true)
	fresh.sort_custom(func(a, b): return a[0] < b[0])
	set_value("Edit automation curve", lane.curve, "points", fresh)

func set_all(key: String, value) -> void:
	var ids = state.selected_instruments.duplicate()
	state.document.transact("Set selected " + key, func():
		for id in ids:
			var instrument = state.find_instrument(id)
			if not instrument.is_empty(): instrument[key] = value)

static func create_instrument(kind: String, sheet: String, start: float, length: float) -> Dictionary:
	return Model.create("Instrument", {"$type": kind, "name": kind, "placement": {"parameter": sheet, "start": start, "length": length}})

static func create_marker(kind: String, position: float, length: float = 2.0) -> Dictionary:
	var marker = Model.create(kind, {"name": "A" if kind == "Destination" else kind, "position": position})
	if is_region(marker): marker.length = length
	return marker

static func is_region(marker: Dictionary) -> bool:
	return marker.get("$type", "") in ["Loop", "TransitionRegion", "Magnet"]

static func clone_item(item: Dictionary) -> Dictionary:
	var copy = item.duplicate(true)
	var old_id = copy.get("id", NONE)
	copy.id = Model.new_id()
	for mod in copy.get("modulators", []):
		mod.id = Model.new_id()
		if mod.get("target", {}).get("owner", NONE) == old_id: mod.target.owner = copy.id
	for instrument in copy.get("transitionInstruments", []):
		var cloned = clone_item(instrument)
		instrument.clear()
		instrument.merge(cloned)
	return copy
