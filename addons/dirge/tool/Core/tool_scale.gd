@tool
extends Node
const Settings = preload("res://addons/dirge/tool/Core/tool_settings.gd")
const SETTING = "dirge/ui_scale"
signal changed
var current: float = 1.0

static func compute() -> float:
	if not Engine.is_editor_hint():
		return 1.0
	var override: float = Settings.get_value(SETTING, 0.0)
	if override > 0.0:
		return override
	var font_size: float = Settings.get_value("interface/editor/fonts/main_font_size", Settings.get_value("interface/editor/main_font_size", 14.0))
	if font_size <= 0.0:
		font_size = 14.0
	return EditorInterface.get_editor_scale() * font_size / 14.0

static func px(authored: float) -> int:
	return maxi(1, roundi(authored * compute()))

func _enter_tree() -> void:
	Settings.register(SETTING, 0.0, PROPERTY_HINT_RANGE, "0,4,0.05,or_greater")
	if Engine.is_editor_hint():
		EditorInterface.get_editor_settings().settings_changed.connect(refresh)
	refresh()

func _exit_tree() -> void:
	if Engine.is_editor_hint() and EditorInterface.get_editor_settings().settings_changed.is_connected(refresh):
		EditorInterface.get_editor_settings().settings_changed.disconnect(refresh)

func _notification(what: int) -> void:
	if what == NOTIFICATION_WM_DPI_CHANGE:
		refresh()

func refresh() -> void:
	var value = compute()
	if current != value:
		current = value
		changed.emit()
