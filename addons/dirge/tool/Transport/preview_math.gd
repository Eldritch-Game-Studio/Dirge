@tool
extends RefCounted
const EPSILON = 0.000001

static func compute(emitter: Dictionary, listener: Dictionary, min_distance: float, max_distance: float) -> Dictionary:
	var to_emitter: Vector3 = emitter.get("position", Vector3.ZERO) - listener.get("position", Vector3.ZERO)
	var distance = to_emitter.length()
	var span = max_distance - min_distance
	var normalized = float(distance >= max_distance) if span <= EPSILON else clampf((distance - min_distance) / span, 0.0, 1.0)
	var up: Vector3 = listener.get("up", Vector3.UP)
	var forward: Vector3 = emitter.get("forward", Vector3.FORWARD)
	var listener_forward: Vector3 = listener.get("forward", Vector3.FORWARD)
	var velocity: Vector3 = emitter.get("velocity", Vector3.ZERO)
	var relative: Vector3 = velocity - listener.get("velocity", Vector3.ZERO)
	var elevation: float = 0.0
	var cone: float = 0.0
	var speed: float = 0.0
	if distance > EPSILON:
		if up.length() > EPSILON:
			elevation = rad_to_deg(asin(clampf(to_emitter.dot(up) / (distance * up.length()), -1.0, 1.0)))
		if forward.length() > EPSILON:
			cone = rad_to_deg(acos(clampf(forward.dot(-to_emitter) / (distance * forward.length()), -1.0, 1.0)))
		speed = relative.dot(to_emitter) / distance
	return {"Distance": distance, "DistanceNormalized": normalized, "Direction": signed_yaw(listener_forward, up, to_emitter), "Elevation": elevation, "EventConeAngle": cone, "EventOrientation": signed_yaw(listener_forward, up, forward), "Speed": speed, "SpeedAbsolute": velocity.length()}

static func signed_yaw(forward: Vector3, up: Vector3, value: Vector3) -> float:
	if up.length() <= EPSILON:
		return 0.0
	up = up.normalized()
	var horizontal = forward - up * forward.dot(up)
	if horizontal.length() <= EPSILON:
		return 0.0
	horizontal = horizontal.normalized()
	var projected = value - up * value.dot(up)
	if projected.length_squared() <= EPSILON * EPSILON:
		return 0.0
	return rad_to_deg(atan2(projected.dot(horizontal.cross(up)), projected.dot(horizontal)))

static func rotate_yaw(direction: Vector3, degrees: float) -> Vector3:
	return direction.rotated(Vector3.UP, deg_to_rad(degrees))

static func to_transform(attributes: Dictionary) -> Transform3D:
	var forward: Vector3 = attributes.get("forward", Vector3.FORWARD)
	var up: Vector3 = attributes.get("up", Vector3.UP)
	var basis = Basis.IDENTITY
	if not forward.is_zero_approx() and not up.is_zero_approx() and not forward.cross(up).is_zero_approx():
		basis = Basis.looking_at(forward, up)
	return Transform3D(basis, attributes.get("position", Vector3.ZERO))
