@tool
extends RefCounted

const HEADER_SECTIONS = "dirge/header_sections"
const BROWSER_SPLIT = "dirge/split_browser"
const BOTTOM_SPLIT = "dirge/split_bottom"

static func has_setting(name: String) -> bool:
	return Engine.is_editor_hint() and EditorInterface.get_editor_settings().has_setting(name)

static func get_value(name: String, fallback: Variant = null) -> Variant:
	if has_setting(name):
		return EditorInterface.get_editor_settings().get_setting(name)
	return fallback

static func set_value(name: String, value: Variant) -> void:
	if Engine.is_editor_hint():
		EditorInterface.get_editor_settings().set_setting(name, value)

static func register(name: String, value: Variant, hint: int = PROPERTY_HINT_NONE, hint_string: String = "") -> void:
	if not Engine.is_editor_hint():
		return
	var settings = EditorInterface.get_editor_settings()
	if not settings.has_setting(name):
		settings.set_setting(name, value)
	settings.set_initial_value(name, value, false)
	settings.add_property_info({"name": name, "type": typeof(value), "hint": hint, "hint_string": hint_string})
