@tool
extends RefCounted

const VERSION = 1
const DEFAULT_PORT = 7447
const MAX_FRAME_BYTES = 64 * 1024 * 1024
const TYPES = ["hello", "stats", "instances", "started", "error", "project", "mixer-set", "start", "stop", "set-param", "snapshot"]
const KEYS = ["type", "protocol", "version", "projectName", "time", "instances", "realVoices", "updateMs", "buses", "momentaryLufs", "shortTermLufs", "integratedLufs", "truePeakDbtp", "name", "left", "right", "handle", "event", "state", "position", "virtual", "path", "message", "json", "bus", "volumeDb", "effect", "value", "immediate", "start"]

static func encode(message: Dictionary) -> PackedByteArray:
	var framed = {"type": message.get("type", ""), "protocol": VERSION}
	for key in message:
		if key != "type" and key != "protocol" and message[key] != null:
			framed[key] = _wire_value(message[key])
	return (JSON.stringify(framed, "", false) + "\n").to_utf8_buffer()

static func decode(line) -> Dictionary:
	var parser = JSON.new()
	var text: String = line.get_string_from_utf8() if line is PackedByteArray else str(line)
	if parser.parse(text) != OK or not parser.data is Dictionary:
		return {}
	var message: Dictionary = _read_value(parser.data)
	if message.get("protocol", VERSION) != VERSION or message.get("type", "") not in TYPES:
		return {}
	message["protocol"] = VERSION
	if not _valid_message(message):
		return {}
	return message

static func _valid_message(message: Dictionary) -> bool:
	var strings: Array = []
	var numbers: Array = []
	var booleans: Array = []
	match message.type:
		"hello": strings = ["version", "projectName"]
		"stats": numbers = ["time", "instances", "realVoices", "updateMs", "momentaryLufs", "shortTermLufs", "integratedLufs", "truePeakDbtp"]
		"started":
			strings = ["path"]
			numbers = ["handle"]
		"error": strings = ["message"]
		"project": strings = ["json"]
		"mixer-set":
			strings = ["bus", "name"]
			numbers = ["volumeDb", "effect", "value"]
		"start": strings = ["path"]
		"stop":
			numbers = ["handle"]
			booleans = ["immediate"]
		"set-param":
			strings = ["name"]
			numbers = ["value", "handle"]
		"snapshot":
			strings = ["name"]
			booleans = ["start"]
	for key in strings:
		if message.get(key) != null and not message[key] is String:
			return false
	for key in numbers:
		if message.get(key) != null and not (message[key] is float or message[key] is int):
			return false
	for key in booleans:
		if message.has(key) and not message[key] is bool:
			return false
	if message.type == "instances" and message.get("instances") != null and not message.instances is Array:
		return false
	if message.type == "stats" and message.get("buses") != null and not message.buses is Array:
		return false
	return true

static func _wire_value(value):
	if value is float:
		if is_nan(value): return "NaN"
		if is_inf(value): return "-Infinity" if value < 0 else "Infinity"
	elif value is Dictionary:
		var result: Dictionary = {}
		for key in value:
			if value[key] != null:
				result[key] = _wire_value(value[key])
		return result
	elif value is Array:
		var result: Array = []
		for item in value:
			result.append(_wire_value(item))
		return result
	return value

static func _read_value(value):
	if value is Dictionary:
		var result: Dictionary = {}
		for key in value:
			var canonical = key
			for known in KEYS:
				if str(key).nocasecmp_to(known) == 0:
					canonical = known
					break
			var item = value[key]
			# Named floats belong only to numeric fields, never an event name/path.
			if canonical in ["time", "updateMs", "momentaryLufs", "shortTermLufs", "integratedLufs", "truePeakDbtp", "left", "right", "position", "volumeDb", "value"] and item is String:
				match item:
					"-Infinity": item = -INF
					"Infinity": item = INF
					"NaN": item = NAN
			result[canonical] = _read_value(item)
		return result
	if value is Array:
		var result: Array = []
		for item in value:
			result.append(_read_value(item))
		return result
	return value

class FrameReader extends RefCounted:
	var _buffer = PackedByteArray()
	var _scanned: int = 0
	var pending: int:
		get: return _buffer.size()

	func reset() -> void:
		_buffer.clear()
		_scanned = 0

	func append(bytes: PackedByteArray) -> bool:
		if _buffer.size() + bytes.size() > 64 * 1024 * 1024:
			reset()
			return false
		_buffer.append_array(bytes)
		return true

	# null means incomplete; an empty byte array is a complete empty frame.
	func read_frame():
		for index in range(_scanned, _buffer.size()):
			if _buffer[index] == 10:
				var frame = _buffer.slice(0, index)
				_buffer = _buffer.slice(index + 1)
				_scanned = 0
				return frame
		_scanned = _buffer.size()
		return null
