@tool
extends RefCounted

# JSON's full-precision numbers are locale independent and preserve every model bit.
static func number(value) -> String:
	if float(value) == 0.0:
		return "0"
	var text: String = JSON.stringify(value, "", false, true)
	if text.ends_with(".0"):
		text = text.left(-2)
	return text

static func _bool(value: bool) -> String:
	return "True" if value else "False"

static func _vector(value: Vector3) -> String:
	return "[%s %s %s]" % [number(value.x), number(value.y), number(value.z)]

static func attributes(value: Dictionary) -> String:
	return "p=%s v=%s f=%s u=%s" % [_vector(value.get("position", Vector3.ZERO)), _vector(value.get("velocity", Vector3.ZERO)), _vector(value.get("forward", Vector3.FORWARD)), _vector(value.get("up", Vector3.UP))]

static func format(command: Dictionary) -> String:
	var type: String = command.get("type", "")
	var voice: String = str(command.get("voice", 0))
	var bus: String = command.get("bus", "")
	match type:
		"VoicePlay":
			var asset = command.get("asset", "")
			if asset is Dictionary:
				asset = asset.get("path", "")
			var spatial: String = "2d, "
			if command.get("spatial", false):
				spatial = "3d %s, %s..%s, " % [attributes(command.get("attributes", {})), number(command.get("minDistance", 1.0)), number(command.get("maxDistance", 10000.0))]
			return "VoicePlay(%s, %s, %s, %s, %s, %s dB, %sx, %ssync %s)" % [voice, asset, bus, number(command.get("startSeconds", 0.0)), "loop" if command.get("loop", false) else "once", number(command.get("volumeDb", 0.0)), number(command.get("pitch", 1.0)), spatial, str(command.get("syncGroup", 0))]
		"VoiceStop":
			return "VoiceStop(%s)" % voice
		"VoiceSeek":
			return "VoiceSeek(%s, %s)" % [voice, number(command.get("seconds", 0.0))]
		"VoiceSetVolume":
			return "VoiceSetVolume(%s, %s)" % [voice, number(command.get("volumeDb", 0.0))]
		"VoiceSetPitch":
			return "VoiceSetPitch(%s, %s)" % [voice, number(command.get("pitch", 1.0))]
		"VoiceSet3D":
			return "VoiceSet3D(%s, %s)" % [voice, attributes(command.get("attributes", {}))]
		"VoiceSetPaused":
			return "VoiceSetPaused(%s, %s)" % [voice, _bool(command.get("paused", false))]
		"BusSetVolume":
			return "BusSetVolume(%s, %s)" % [bus, number(command.get("volumeDb", 0.0))]
		"BusSetMute":
			return "BusSetMute(%s, %s)" % [bus, _bool(command.get("mute", false))]
		"BusSetSolo":
			return "BusSetSolo(%s, %s)" % [bus, _bool(command.get("solo", false))]
		"BusSetBypass":
			return "BusSetBypass(%s, %s)" % [bus, _bool(command.get("bypass", false))]
		"EffectSetEnabled":
			return "EffectSetEnabled(%s, %d, %s)" % [bus, command.get("effectIndex", 0), _bool(command.get("enabled", true))]
		"EffectSetParameter":
			return "EffectSetParameter(%s, %d, %s, %s)" % [bus, command.get("effectIndex", 0), command.get("parameter", ""), number(command.get("value", 0.0))]
	assert(false, "Unknown command type: " + type)
	return ""
