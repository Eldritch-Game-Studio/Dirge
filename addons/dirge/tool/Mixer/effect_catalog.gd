@tool
extends RefCounted
## Engine reflection keeps the editor and the actual installed DSP classes in sync.
const Ops = preload("res://addons/dirge/tool/Mixer/mixer_graph_ops.gd")
const NATIVE_TYPES = ["DirgeAudioEffectSend", "DirgeAudioEffectReturn", "DirgeAudioEffectConvolutionReverb"]
static var _parameters: Dictionary = {}

static func available_types() -> Array[String]:
	var types: Array[String] = []
	for type in ClassDB.get_inheriters_from_class("AudioEffect"):
		if ClassDB.can_instantiate(type) and type not in NATIVE_TYPES and type not in ["AudioEffectCapture", "AudioEffectSpectrumAnalyzer", "AudioEffectRecord"]:
			types.append(str(type))
	types.sort()
	for type in NATIVE_TYPES:
		if ClassDB.class_exists(type) and ClassDB.can_instantiate(type):
			types.append(type)
	return types

static func display_name(effect_or_type) -> String:
	if effect_or_type is Dictionary:
		var source = effect_or_type.get("stringParameters", {}).get("sidechain", "")
		if effect_or_type.get("typeName", "") == "AudioEffectCompressor" and not source.is_empty():
			return "Ducking (from " + source + ")"
		return display_name(effect_or_type.get("typeName", ""))
	var type = str(effect_or_type)
	var native = type.begins_with("Dirge")
	var name = type.trim_prefix("Dirge").trim_prefix("AudioEffect")
	return ("Dirge " if native else "") + name.to_snake_case().capitalize()

static func create(type: String) -> Dictionary:
	return {"id": Ops.new_id(), "typeName": type, "enabled": true, "parameters": {}, "stringParameters": {}}

static func ducking_preset(source_bus: String) -> Dictionary:
	var effect = create("AudioEffectCompressor")
	effect.parameters = {"threshold": -24.0, "ratio": 8.0, "attack_us": 20000.0, "release_ms": 250.0, "gain": 0.0}
	effect.stringParameters = {"sidechain": source_bus}
	return effect

static func parameters(type: String) -> Array:
	if _parameters.has(type):
		return _parameters[type]
	var result: Array = []
	if not ClassDB.class_exists(type) or not ClassDB.can_instantiate(type) or not ClassDB.is_parent_class(type, "AudioEffect"):
		return result
	var defaults = ClassDB.instantiate(type)
	for property in defaults.get_property_list():
		var usage = int(property.get("usage", 0))
		var name = str(property.name)
		if usage & PROPERTY_USAGE_EDITOR == 0 or usage & (PROPERTY_USAGE_GROUP | PROPERTY_USAGE_SUBGROUP | PROPERTY_USAGE_CATEGORY) != 0:
			continue
		if name in ["resource_name", "resource_path", "resource_local_to_scene", "script"]:
			continue
		var variant_type = int(property.type)
		if variant_type not in [TYPE_BOOL, TYPE_INT, TYPE_FLOAT, TYPE_STRING, TYPE_STRING_NAME]:
			continue
		var hint = int(property.get("hint", PROPERTY_HINT_NONE))
		var hint_string = str(property.get("hint_string", ""))
		var current = defaults.get(name)
		var descriptor = {"name": name, "label": name.replace("/", " ").capitalize(), "kind": "Float", "min": 0.0, "max": 1.0, "step": 0.01, "default": current, "enumLabels": [], "enumValues": [], "hint": hint, "hintString": hint_string}
		match variant_type:
			TYPE_STRING, TYPE_STRING_NAME:
				descriptor.kind = "String"
			TYPE_BOOL:
				descriptor.kind = "Bool"
				descriptor.default = 1.0 if current else 0.0
				descriptor.step = 1.0
			TYPE_INT, TYPE_FLOAT:
				if variant_type == TYPE_INT and hint == PROPERTY_HINT_ENUM:
					descriptor.kind = "Enum"
					var next = 0
					for option in hint_string.split(","):
						var pair = option.split(":")
						if pair.size() > 1:
							next = int(pair[1])
						descriptor.enumLabels.append(pair[0].strip_edges())
						descriptor.enumValues.append(next)
						next += 1
				else:
					descriptor.kind = "Int" if variant_type == TYPE_INT else "Float"
					if variant_type == TYPE_INT:
						descriptor.max = 100.0
						descriptor.step = 1.0
					elif name.ends_with("_db"):
						descriptor.min = -80.0
						descriptor.max = 24.0
						descriptor.step = 0.1
					elif name.ends_with("_hz"):
						descriptor.min = 1.0
						descriptor.max = 20500.0
						descriptor.step = 1.0
					elif name.ends_with("_ms"):
						descriptor.max = 2000.0
						descriptor.step = 1.0
					if hint == PROPERTY_HINT_RANGE:
						var parts = hint_string.split(",")
						if parts.size() > 0 and parts[0].is_valid_float(): descriptor.min = float(parts[0])
						if parts.size() > 1 and parts[1].is_valid_float(): descriptor.max = float(parts[1])
						if parts.size() > 2 and parts[2].is_valid_float() and float(parts[2]) > 0: descriptor.step = float(parts[2])
					else:
						descriptor.min = minf(descriptor.min, float(current))
						descriptor.max = maxf(descriptor.max, float(current))
		result.append(descriptor)
	_parameters[type] = result
	return result
