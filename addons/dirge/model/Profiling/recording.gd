@tool
extends RefCounted

static func create(project_name: String = "", seed: int = 0, platform = null, locale: String = "") -> Dictionary:
	return {"projectName": project_name, "seed": seed, "platform": platform, "locale": locale, "ticks": []}

static func tick() -> Dictionary:
	return {"dt": 0.0, "time": 0.0, "calls": [], "programmerSounds": [], "commands": [], "ended": [], "instanceCount": 0, "realVoiceCount": 0}

static func api_call(method: String = "", target: String = "System") -> Dictionary:
	return {"method": method, "target": target, "handle": -1, "result": -1, "name": null, "text": null, "value": 0.0, "flag": false, "mode": 0, "vector": null}

static func pack(attributes: Dictionary) -> Array:
	var result: Array = []
	for field in ["position", "velocity", "forward", "up"]:
		var default: Vector3 = Vector3.FORWARD if field == "forward" else Vector3.UP if field == "up" else Vector3.ZERO
		var vector: Vector3 = attributes.get(field, default)
		result.append_array([vector.x, vector.y, vector.z])
	return result

static func unpack(vector) -> Dictionary:
	if vector == null or vector.size() < 12:
		return {"position": Vector3.ZERO, "velocity": Vector3.ZERO, "forward": Vector3.FORWARD, "up": Vector3.UP}
	return {"position": Vector3(vector[0], vector[1], vector[2]), "velocity": Vector3(vector[3], vector[4], vector[5]), "forward": Vector3(vector[6], vector[7], vector[8]), "up": Vector3(vector[9], vector[10], vector[11])}
