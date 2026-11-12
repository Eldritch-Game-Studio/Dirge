@tool
extends RefCounted

const ModEval = preload("mod_eval.gd")
const Instance = preload("event_instance.gd")
const EPSILON = 1e-9

# The system owns descriptions; back-references must not create RefCounted cycles.
var _system_ref: WeakRef
var system:
	get: return _system_ref.get_ref() if _system_ref != null else null
	set(value): _system_ref = weakref(value) if value != null else null
var event: Dictionary
var id: String:
	get: return event.get("id", "")
var path: String:
	get: return event.get("path", "")
var is_3d: bool:
	get: return event.get("macros", {}).get("is3D", false)
var parameters: Array:
	get: return event.get("localParameters", [])
var is_oneshot: bool = true
var length: float = 0.0
var instance_count: int = 0
var instruments: Array = []
var points: Array = []
var magnets: Array = []
var tempo: Array = []
var marker_positions: Dictionary = {}
var volume_lanes: Array
var pitch_lanes: Array
var modulators: Array
var max_release: float
var bus_name: String = "Master"
var bus_index: int = 0
var last_start_time: float = -INF
var started: Array = []
var _pool: Array = []

func _init(root, authored: Dictionary):
	system = root
	event = authored
	var buses = system.project.get("mixer", {}).get("buses", [])
	for i in range(buses.size()):
		var bus = buses[i]
		if bus.get("id", "") == event.get("bus", "") or (ModEval.is_none(event.get("bus")) and ModEval.is_none(bus.get("parent"))):
			bus_name = bus.get("name", "Master")
			bus_index = i
			if not ModEval.is_none(event.get("bus")):
				break
	is_oneshot = not event.get("macros", {}).get("persistent", false)
	for track in event.get("tracks", []):
		for instrument in track.get("instruments", []):
			var info = _instrument_info(track, instrument)
			instruments.append(info)
			if info.on_timeline:
				length = maxf(length, info.region_start if is_inf(info.region_end) else info.region_end)
				if instrument.get("loop", false):
					is_oneshot = false
	for marker in event.get("markers", []):
		var position = float(marker.get("position", 0))
		marker_positions[marker.get("id", "")] = position
		match marker.get("$type", ""):
			"Loop":
				is_oneshot = false
				_add_point(position + maxf(0, marker.get("length", 0)), marker, 1)
			"TransitionRegion":
				_add_point(position, marker, 2)
				_add_point(position + maxf(0, marker.get("length", 0)), marker, 3)
			"Magnet":
				magnets.append(marker)
				length = maxf(length, position + maxf(0, marker.get("length", 0)))
			_: _add_point(position, marker, 0)
		if marker.get("transitionLength", 0) > 0:
			for instrument in marker.get("transitionInstruments", []):
				instruments.append(_instrument_info({}, instrument, marker))
	points.sort_custom(func(a, b): return a.position < b.position if a.position != b.position else a.kind < b.kind)
	for marker in event.get("tempo", []):
		if marker.get("bpm", 120) > 0:
			tempo.append(marker)
	tempo.sort_custom(func(a, b): return a.get("position", 0) < b.get("position", 0))
	volume_lanes = ModEval.lanes(event.get("automation", []), "EventVolumeDb", id)
	pitch_lanes = ModEval.lanes(event.get("automation", []), "EventPitch", id)
	modulators = ModEval.collect(event.get("modulators", []), "EventVolumeDb", "EventPitch", id)
	max_release = ModEval.max_release(modulators)

func _add_point(position: float, marker: Dictionary, kind: int) -> void:
	points.append({"position": position, "marker": marker, "kind": kind})
	length = maxf(length, position)

func _instrument_info(track: Dictionary, instrument: Dictionary, transition = null) -> Dictionary:
	var placement = instrument.get("placement", {})
	var on_timeline = transition != null or ModEval.is_none(placement.get("parameter"))
	var kind = instrument.get("$type", "Silence")
	var natural = 0.0
	if kind == "Clip":
		natural = system.asset_length(instrument.get("asset", ""))
	elif kind == "Multi":
		for entry in instrument.get("entries", []):
			natural = maxf(natural, system.asset_length(entry.get("asset", "")))
	var start = float(placement.get("start", 0))
	var duration = float(placement.get("length", 0))
	var point = on_timeline and (kind == "Command" or (kind == "Silence" and duration <= 0))
	var end = start + maxf(0, duration)
	if on_timeline:
		if point: end = start
		elif duration > 0: end = start + duration
		elif natural > 0 and not instrument.get("loop", false): end = start + natural
		else: end = INF
	var mods = ModEval.collect(instrument.get("modulators", []), "InstrumentVolumeDb", "InstrumentPitch", instrument.get("id", ""))
	return {"instrument": instrument, "track": track, "kind": kind, "on_timeline": on_timeline,
		"region_start": start, "region_end": end, "is_point": point, "transition": transition,
		"volume_lanes": ModEval.lanes(event.get("automation", []), "InstrumentVolumeDb", instrument.get("id", "")),
		"pitch_lanes": ModEval.lanes(event.get("automation", []), "InstrumentPitch", instrument.get("id", "")),
		"modulators": mods, "max_release": ModEval.max_release(mods)}

func create_instance(parent = null):
	var instance = _pool.pop_back() if not _pool.is_empty() else Instance.new(system, self)
	instance.reinitialize()
	if parent != null:
		instance.parent = parent
		instance.depth = parent.depth + 1
	instance_count += 1
	system.instance_count += 1
	if parent == null:
		system.instances.append(instance)
		if system.recorder != null:
			system.recorder.created(instance, "CreateInstance", path)
	return instance

func recycle(instance) -> void:
	instance_count -= 1
	system.instance_count -= 1
	started.erase(instance)
	_pool.append(instance)

func apply_virtualize_stealing() -> void:
	var macros = event.get("macros", {})
	if macros.get("stealing", "Oldest") == "Virtualize" and macros.get("maxInstances", 0) > 0:
		for i in range(started.size()):
			started[i].steal_virtual = i >= macros.maxInstances

func stop_all(mode: String = "AllowFadeout") -> void:
	for instance in started.duplicate():
		instance.stop(mode)

func magnetize(position: float) -> float:
	for magnet in magnets:
		var start = magnet.get("position", 0.0)
		if position >= start - EPSILON and position < start + magnet.get("length", 0) - EPSILON:
			return start
	return position

func tempo_segment(position: float) -> int:
	var result = -1
	for i in range(tempo.size()):
		if tempo[i].get("position", 0) > position + EPSILON:
			break
		result = i
	return result

func bpm_at(position: float) -> float:
	var segment = tempo_segment(position)
	return tempo[segment].get("bpm", 120) if segment >= 0 else 0.0

func next_grid(position: float, quantization: String, inclusive: bool):
	if quantization in ["Beat", "Bar"]:
		var grid = next_beat(position, inclusive, quantization == "Bar")
		return grid.position if not grid.is_empty() else null
	if quantization == "Marker":
		for point in points:
			if point.kind != 0 or point.marker.get("$type", "") != "Destination":
				continue
			if (point.position >= position - EPSILON if inclusive else point.position > position + EPSILON):
				return point.position
		return null
	return position

func next_beat(after: float, inclusive: bool, bars: bool = false) -> Dictionary:
	if tempo.is_empty():
		return {}
	var segment = tempo_segment(after)
	if segment < 0:
		return {"position": tempo[0].get("position", 0), "bpm": tempo[0].get("bpm", 120), "bar": 1, "beat": 1}
	var marker = tempo[segment]
	var beats_per_bar = maxi(1, marker.get("beatsPerBar", 4))
	var unit = 60.0 / marker.get("bpm", 120) * (beats_per_bar if bars else 1)
	var k = ceil((after - marker.get("position", 0)) / unit - 0.0000001)
	var candidate = marker.get("position", 0) + k * unit
	if not inclusive and candidate <= after + EPSILON:
		k += 1
		candidate += unit
	if segment + 1 < tempo.size() and candidate >= tempo[segment + 1].get("position", 0) - EPSILON:
		return {"position": tempo[segment + 1].get("position", 0), "bpm": tempo[segment + 1].get("bpm", 120), "bar": 1, "beat": 1}
	var index = maxi(0, int(k))
	return {"position": candidate, "bpm": marker.get("bpm", 120), "bar": index + 1 if bars else int(index / beats_per_bar) + 1, "beat": 1 if bars else index % beats_per_bar + 1}
