@tool
extends RefCounted

const EPSILON = 1e-9
var _instance_ref: WeakRef
var _description_ref: WeakRef
var instance:
	get: return _instance_ref.get_ref() if _instance_ref != null else null
	set(value): _instance_ref = weakref(value) if value != null else null
var description:
	get: return _description_ref.get_ref() if _description_ref != null else null
	set(value): _description_ref = weakref(value) if value != null else null
var position: float = 0.0
var holding: bool = false
var transition = null
var transition_elapsed: float = 0.0
var _transition_length: float = 0.0
var _transition_destination: float = 0.0
var _inclusive: bool = true
var _pending = null
var _armed = null
var _armed_at: float = 0.0
var _last_beat: float = 0.0
var _beat_inclusive: bool = true
var _jumps: int = 0
var _advance_remainder: float = 0.0

func _init(owner, desc):
	instance = owner
	description = desc

func reset(at: float) -> void:
	if transition != null:
		var old = transition
		transition = null
		instance.on_transition_end(old)
	position = description.magnetize(at)
	holding = false
	_inclusive = true
	_pending = null
	_armed = null
	_last_beat = position
	_beat_inclusive = true
	_jumps = 0

func begin() -> bool:
	_jumps = 0
	_advance_remainder = 0
	_process_beats(position)
	_arm_containing(position)
	return _jumps > 0

func key_off() -> void:
	if holding:
		holding = false
		_inclusive = false

func advance(dt: float) -> void:
	if holding or dt <= 0:
		return
	_jumps = 0
	var remaining = dt
	_advance_remainder = dt
	var points = description.points
	for _guard in range(4096):
		if remaining <= 0 or _jumps > 64:
			return
		if transition != null:
			var step = minf(remaining, _transition_length - transition_elapsed)
			var previous = transition_elapsed
			transition_elapsed += step
			remaining -= step
			_advance_remainder = maxf(0, remaining)
			instance.on_transition_segment(transition, previous, transition_elapsed)
			if transition_elapsed < _transition_length - EPSILON:
				return
			var old = transition
			transition = null
			instance.on_transition_end(old)
			_jump(_transition_destination)
			if holding:
				return
			continue
		var target = position + remaining
		var pi = _next_point_index()
		var next = points[pi].position if pi >= 0 else INF
		var kind = 0
		if _pending != null and _pending.at <= next:
			next = _pending.at
			kind = 1
		if _armed != null and _armed_at <= next:
			next = _armed_at
			kind = 2
		if next > target + EPSILON:
			_move_to(target, false)
			return
		var arrived_by_jump = _inclusive and next <= position + EPSILON
		var points_here = pi >= 0 and points[pi].position <= next + EPSILON
		var looping_here = not arrived_by_jump and points_here and _loop_fires_at(pi, next)
		remaining -= maxf(0, next - position)
		_advance_remainder = maxf(0, remaining)
		_move_to(next, looping_here)
		var transitioned = false
		if kind == 1:
			var pending = _pending
			_pending = null
			_begin_transition(pending.marker, pending.destination)
			transitioned = true
		elif kind == 2:
			var region = _armed
			var destination = description.marker_positions.get(region.get("destination", ""))
			if instance.marker_passes(region) and destination != null:
				_begin_transition(region, destination)
				transitioned = true
			else:
				var grid = description.next_grid(position, region.get("quantization", "Beat"), false)
				if grid == null:
					_armed = null
				else:
					_armed_at = grid
		if not transitioned and points_here:
			for j in range(pi, points.size()):
				if points[j].position > next + EPSILON or _handle_point(points[j], arrived_by_jump):
					break
		if holding:
			return

func _next_point_index() -> int:
	for i in range(description.points.size()):
		var at = description.points[i].position
		if (at >= position - EPSILON if _inclusive else at > position + EPSILON):
			return i
	return -1

func _loop_fires_at(first: int, at: float) -> bool:
	for i in range(first, description.points.size()):
		var point = description.points[i]
		if point.position > at + EPSILON:
			break
		if point.kind == 1 and point.marker.get("probability", 1.0) >= 1 and instance.conditions_pass(point.marker.get("conditions", [])):
			return true
	return false

func _begin_transition(marker: Dictionary, destination: float) -> void:
	var duration = marker.get("transitionLength", 0.0)
	if duration <= 0:
		_jump(destination)
		return
	_jumps += 1
	if _jumps > 64:
		return
	transition = marker
	transition_elapsed = 0.0
	_transition_length = duration
	_transition_destination = destination
	_pending = null
	_armed = null
	instance.on_transition_begin(marker)

func _move_to(at: float, looping_here: bool) -> void:
	var previous = position
	var inclusive = _inclusive
	position = at
	_inclusive = false
	_process_beats(at)
	instance.on_timeline_segment(previous, at, inclusive, looping_here)

func _process_beats(up_to: float) -> void:
	for _guard in range(4096):
		var beat = description.next_beat(_last_beat, _beat_inclusive)
		if beat.is_empty() or beat.position > up_to + EPSILON:
			break
		_last_beat = beat.position
		_beat_inclusive = false
		instance.raise_callback(8, {"type": "BeatCallback", "bar": beat.bar, "beat": beat.beat, "bpm": beat.bpm, "position": beat.position})

func _handle_point(point: Dictionary, arrived_by_jump: bool) -> bool:
	var marker = point.marker
	match point.kind:
		0:
			match marker.get("$type", ""):
				"Destination":
					instance.raise_callback(4, {"type": "MarkerCallback", "name": marker.get("name", ""), "position": marker.get("position", 0)})
				"Transition":
					if not instance.marker_passes(marker):
						return false
					var destination = description.marker_positions.get(marker.get("destination", ""))
					if destination == null:
						return false
					var grid = description.next_grid(position, marker.get("quantization", "None"), true)
					if marker.get("quantization", "None") == "None" or grid == null or grid <= position + EPSILON:
						_begin_transition(marker, destination)
						return true
					_pending = {"at": grid, "destination": destination, "marker": marker}
				"SustainPoint":
					if instance.marker_passes(marker):
						holding = true
						instance.state = "Sustaining"
						return true
		1:
			if not arrived_by_jump and instance.marker_passes(marker):
				_jump(marker.get("position", 0))
				return true
		2: return _arm(marker)
		3:
			if not arrived_by_jump and _armed == marker:
				_armed = null
	return false

func _arm(region: Dictionary) -> bool:
	if region.get("quantization", "Beat") == "None":
		var destination = description.marker_positions.get(region.get("destination", ""))
		if instance.marker_passes(region) and destination != null:
			_begin_transition(region, destination)
			return true
		return false
	var grid = description.next_grid(position, region.get("quantization", "Beat"), true)
	if grid != null:
		_armed = region
		_armed_at = grid
	return false

func _arm_containing(at: float) -> void:
	for point in description.points:
		if point.kind != 2:
			continue
		var region = point.marker
		if at >= region.get("position", 0) - EPSILON and at < region.get("position", 0) + region.get("length", 0) - EPSILON:
			if _arm(region):
				return

func _jump(destination: float) -> void:
	_jumps += 1
	if _jumps > 64:
		return
	position = destination
	_inclusive = true
	_pending = null
	_armed = null
	_last_beat = destination
	_beat_inclusive = true
	_process_beats(destination)
	instance.on_timeline_jump(destination, _advance_remainder)
	_arm_containing(destination)
