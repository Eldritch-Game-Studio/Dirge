@tool
extends RefCounted

# Runtime callers may supply named points without hydrating a whole project.
# Read both input forms without allocating or mutating curves on the audio update path.
static func _x(point: Variant) -> float:
	return point.x if point is Dictionary else point[0]

static func _y(point: Variant) -> float:
	return point.y if point is Dictionary else point[1]

static func _shape(point: Variant) -> String:
	return point.get("shape", "Linear") if point is Dictionary else (point[2] if point.size() > 2 else "Linear")

static func evaluate(curve: Dictionary, x: float) -> float:
	var points: Array = curve.get("points", [])
	if points.is_empty(): return 0.0
	if x <= _x(points[0]): return _y(points[0])
	if x >= _x(points[-1]): return _y(points[-1])
	var hi = 1
	while _x(points[hi]) < x: hi += 1
	var a = points[hi - 1]
	var b = points[hi]
	var start_x = _x(a)
	var start_y = _y(a)
	var span = _x(b) - start_x
	var t: float = 1.0 if span <= 0 else (x - start_x) / span
	match _shape(a):
		"Stepped": return start_y
		"Smooth": t = t * t * (3.0 - 2.0 * t)
	return start_y + (_y(b) - start_y) * t

static func db_to_linear(db: float) -> float:
	return 0.0 if db <= -80.0 else pow(10.0, db / 20.0)

static func linear_to_db(linear: float) -> float:
	return -80.0 if linear <= 0.0001 else 20.0 * log(linear) / log(10.0)
