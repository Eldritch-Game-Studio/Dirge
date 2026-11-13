@tool
extends RefCounted
const Model = preload("../Project/project_model.gd")
static var last_error: String = ""
const ID_KEYS = ["id", "parent", "bus", "parameter", "event", "targetEvent", "targetParameter", "snapshot", "destination", "owner"]

const ENUMS = {
	"ParameterDefinition.kind": ["Continuous", "Discrete", "Labeled"],
	"builtIn": ["None", "Distance", "DistanceNormalized", "Direction", "Elevation", "EventConeAngle", "EventOrientation", "Speed", "SpeedAbsolute"],
	"Snapshot.mode": ["Blending", "Overriding"],
	"Multi.mode": ["Random", "Shuffle", "Sequential"],
	"Scatterer.mode": ["Random", "Shuffle", "Sequential"],
	"shape": ["Sine", "Square", "Triangle", "SawUp", "SawDown", "Noise"],
	"stealing": ["Oldest", "Quietest", "Furthest", "Virtualize", "None"],
	"rolloff": ["Inverse", "InverseSquare", "Logarithmic", "Disabled"],
	"startQuantization": ["None", "Beat", "Bar", "Marker"],
	"quantization": ["None", "Beat", "Bar", "Marker"],
	"cutMode": ["Cut", "FadeOut"],
	"action": ["StartEvent", "StopEvent", "SetParameter"]
}
static func deserialize(text: String) -> Dictionary:
	last_error = ""
	var parser = JSON.new()
	if parser.parse(text) != OK:
		last_error = "Line %d: %s" % [parser.get_error_line(), parser.get_error_message()]
		return {}
	if not parser.data is Dictionary:
		last_error = "Project document must be an object."
		return {}
	if not _validate_object(parser.data, "AudioProject"): return {}
	return Model.hydrate(parser.data)

static func serialize(project: Dictionary, indented: bool = true) -> String:
	return JSON.stringify(_ordered(Model.hydrate(project)), "  " if indented else "", false, true)

static func load_file(path: String) -> Dictionary:
	var file = FileAccess.open(path, FileAccess.READ)
	if file == null:
		last_error = "Cannot read %s: %s" % [path, error_string(FileAccess.get_open_error())]
		return {}
	return deserialize(file.get_as_text())

static func save_file(path: String, project: Dictionary) -> Error:
	var file = FileAccess.open(path, FileAccess.WRITE)
	if file == null: return FileAccess.get_open_error()
	file.store_string(serialize(project))
	return file.get_error()

static func _fail(message: String) -> bool:
	last_error = message
	return false

static func _valid_id(value: Variant) -> bool:
	if not value is String or value.length() != 32: return false
	for c in value.to_lower():
		if not c in "0123456789abcdef": return false
	return true

static func _validate_object(value: Dictionary, kind: String) -> bool:
	if kind in ["Instrument", "Modulator", "LogicMarker"]:
		var choices = Model.INSTRUMENT_TYPES if kind == "Instrument" else (Model.MODULATOR_TYPES if kind == "Modulator" else Model.MARKER_TYPES)
		if not value.get("$type", "") in choices: return _fail("Unknown or missing %s discriminator." % kind)
		kind = value["$type"]
		if kind == "Snapshot": kind = "SnapshotInstrument"
		if kind == "Ahdsr": kind = "AhdsrModulator"
	var template = Model.defaults(kind)
	for key in value:
		var item = value[key]
		if not template.has(key): continue # Preserve unknown extension data, without interpreting it.
		var choices: Array = ENUMS.get(kind + "." + key, ENUMS.get(key, []))
		if not choices.is_empty():
			if item is float or item is int:
				if item != int(item): return _fail("%s must be an enum name or integer." % key)
				if item >= 0 and item < choices.size(): value[key] = choices[int(item)]
			elif item is String:
				var resolved = ""
				for choice in choices:
					if choice.to_lower() == item.strip_edges().to_lower(): resolved = choice; break
				if resolved == "": return _fail("Unknown %s '%s'." % [key, item])
				value[key] = resolved
			else: return _fail("%s must be an enum name or integer." % key)
			continue
		if key in ID_KEYS:
			if not _valid_id(item): return _fail("%s must be a 32-hex DirgeId." % key)
			value[key] = item.to_lower()
		elif key == "target":
			if not item is Dictionary or not item.has("kind"): return _fail("PropertyRef must contain kind.")
			if not item.kind in Model.PROPERTY_KINDS: return _fail("Unknown PropertyKind.")
			for member in item:
				if not member in ["kind", "owner", "effectIndex", "name"]: return _fail("Unknown PropertyRef member '%s'." % member)
			if item.has("owner") and not _valid_id(item.owner): return _fail("PropertyRef owner must be a DirgeId.")
			if item.has("effectIndex") and (not (item.effectIndex is float or item.effectIndex is int) or item.effectIndex != int(item.effectIndex)): return _fail("PropertyRef effectIndex must be an integer.")
			if item.get("name") != null and not item.name is String: return _fail("PropertyRef name must be a string.")
			item.owner = item.get("owner", Model.NONE).to_lower()
		elif key == "points" and kind == "Curve":
			if not item is Array: return _fail("Curve points must be an array.")
			for point in item:
				if not point is Array or point.size() < 2 or point.size() > 3: return _fail("CurvePoint must be [x, y, shape].")
				if not (point[0] is float or point[0] is int) or not (point[1] is float or point[1] is int): return _fail("CurvePoint coordinates must be numbers.")
				if point.size() == 3 and not point[2] in ["Linear", "Smooth", "Stepped"]: return _fail("Unknown CurveShape.")
		elif key == "asset":
			if item != null and not item is String: return _fail("AssetRef must be a string.")
		else:
			var expected = template[key]
			if expected is Array and not item is Array: return _fail("%s must be an array." % key)
			if expected is Dictionary and not item is Dictionary:
				# Get-only dictionaries are cleared by the original null copying setter.
				if item == null and key in ["assetLengths", "entries", "parameters", "stringParameters"]: value[key] = {}; continue
				return _fail("%s must be an object." % key)
			if expected is bool and not item is bool: return _fail("%s must be a boolean." % key)
			if (expected is float or expected is int) and not (item is float or item is int): return _fail("%s must be a number." % key)
			if expected is int and (item != int(item) or item < -2147483648 or item > 2147483647): return _fail("%s must be a 32-bit integer." % key)
			if expected is String and item != null and not item is String: return _fail("%s must be a string." % key)
			if key in ["tags", "labels"]:
				for entry in item:
					if entry != null and not entry is String: return _fail("%s entries must be strings." % key)
			if key in ["assetLengths", "parameters", "stringParameters"]:
				for entry in item.values():
					if key == "stringParameters":
						if entry != null and not entry is String: return _fail("String parameter must be a string.")
					elif not (entry is float or entry is int): return _fail("%s values must be numbers." % key)
			if kind == "AudioTable" and key == "entries":
				for locales in item.values():
					if not locales is Dictionary: return _fail("Audio table locales must be an object.")
					for asset in locales.values():
						if asset != null and not asset is String: return _fail("AssetRef must be a string.")
			var child = Model.child_kind(kind, key)
			if child != "":
				if item is Dictionary and not _validate_object(item, child): return false
				if item is Array:
					for entry in item:
						if not entry is Dictionary or not _validate_object(entry, child): return _fail(last_error if last_error != "" else "%s entries must be objects." % key)
			if (kind == "AudioBank" and key == "events") or (kind == "Bus" and key == "vcas"):
				for i in item.size():
					if not _valid_id(item[i]): return _fail("%s entries must be DirgeIds." % key)
					item[i] = item[i].to_lower()
	return true

static func _rank(key: String) -> int:
	if key == "$type": return -1
	if key == "id": return 0
	if key == "name" or key == "path": return 1
	return 2

static func _ordered(value: Variant, dictionary_keys: bool = false, property: bool = false) -> Variant:
	# JSON has one number type; emit integral values identically before and after parsing.
	if value is float and is_finite(value) and value == floor(value) and value >= -9223372036854775808.0 and value < 9223372036854775808.0:
		return int(value)
	if value is Array:
		var items = []
		for entry in value: items.append(_ordered(entry))
		return items
	if not value is Dictionary: return value
	var result = {}
	if property:
		result.kind = value.kind
		result.owner = value.get("owner", Model.NONE)
		if value.get("effectIndex", -1) != -1: result.effectIndex = value.effectIndex
		if value.get("name") != null: result.name = value.name
		return result
	var keys: Array[String] = []
	for key in value: keys.append(String(key))
	if dictionary_keys: keys.sort()
	else: keys.sort_custom(func(a: String, b: String): return a < b if _rank(a) == _rank(b) else _rank(a) < _rank(b))
	for key in keys:
		result[key] = _ordered(value[key], dictionary_keys or key in ["assetLengths", "entries", "parameters", "stringParameters"], key == "target" and not dictionary_keys)
	return result
