@tool
extends RefCounted

static func origin() -> Dictionary:
	return {"position": Vector3.ZERO, "velocity": Vector3.ZERO, "forward": Vector3.FORWARD, "up": Vector3.UP}

static func attributes(value: Dictionary) -> Dictionary:
	var result = origin()
	for key in result:
		var v = value.get(key, result[key])
		if v is Vector3:
			result[key] = v
		elif v is Dictionary:
			result[key] = Vector3(v.get("x", 0), v.get("y", 0), v.get("z", 0))
		elif v is Array and v.size() >= 3:
			result[key] = Vector3(v[0], v[1], v[2])
	return result

static func compute(kind: String, emitter: Dictionary, listener: Dictionary, minimum: float, maximum: float) -> float:
	var delta: Vector3 = emitter.position - listener.position
	var distance = delta.length()
	match kind:
		"Distance": return distance
		"DistanceNormalized":
			if maximum - minimum <= 0.000001:
				return 1.0 if distance >= maximum else 0.0
			return clampf((distance - minimum) / (maximum - minimum), 0, 1)
		"Direction": return signed_yaw(listener.forward, listener.up, delta)
		"Elevation":
			var denominator = distance * listener.up.length()
			return rad_to_deg(asin(clampf(delta.dot(listener.up) / denominator, -1, 1))) if denominator > 0.000001 else 0.0
		"EventConeAngle":
			var denominator = distance * emitter.forward.length()
			return rad_to_deg(acos(clampf(emitter.forward.dot(-delta) / denominator, -1, 1))) if denominator > 0.000001 else 0.0
		"EventOrientation": return signed_yaw(listener.forward, listener.up, emitter.forward)
		"Speed": return (emitter.velocity - listener.velocity).dot(delta) / distance if distance > 0.000001 else 0.0
		"SpeedAbsolute": return emitter.velocity.length()
	return 0.0

static func signed_yaw(forward: Vector3, up: Vector3, vector: Vector3) -> float:
	if up.length() <= 0.000001:
		return 0.0
	up = up.normalized()
	var flat = forward - up * forward.dot(up)
	if flat.length() <= 0.000001:
		return 0.0
	flat = flat.normalized()
	var projected = vector - up * vector.dot(up)
	if projected.length_squared() <= 0.000000000001:
		return 0.0
	return rad_to_deg(atan2(projected.dot(flat.cross(up)), projected.dot(flat)))
