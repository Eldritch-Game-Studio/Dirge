@tool
extends EditorPlugin
const MainScreen = preload("res://addons/dirge/tool/Shell/dirge_main_screen.gd")
const AUTOLOAD_NAME = "DirgeServer"
const AUTOLOAD_SCRIPT = "res://addons/dirge/runtime/dirge_server.gd"
const OVERLAY_NAME = "DirgeOverlay"
const OVERLAY_SCRIPT = "res://addons/dirge/runtime/dirge_overlay.gd"
const ICON_PATH = "res://addons/dirge/tool/ui/icons/dirge.svg"
var main_screen: Control
var _visible_requested: bool = false

func _enter_tree() -> void:
	if not ProjectSettings.has_setting("dirge/project_path"):
		ProjectSettings.set_setting("dirge/project_path", "")
		ProjectSettings.set_initial_value("dirge/project_path", "")
		ProjectSettings.add_property_info({"name": "dirge/project_path", "type": TYPE_STRING, "hint": PROPERTY_HINT_FILE, "hint_string": "*.json"})
	if needs_autoload(AUTOLOAD_NAME): add_autoload_singleton(AUTOLOAD_NAME, AUTOLOAD_SCRIPT)
	if needs_autoload(OVERLAY_NAME): add_autoload_singleton(OVERLAY_NAME, OVERLAY_SCRIPT)
	var filesystem = EditorInterface.get_resource_filesystem()
	filesystem.filesystem_changed.connect(_initialize_main_screen)
	_initialize_main_screen.call_deferred()

static func _import_ready(path: String) -> bool:
	var directory = EditorInterface.get_resource_filesystem().get_filesystem_path(path.get_base_dir())
	if directory == null: return false
	for index in directory.get_file_count():
		if directory.get_file(index) == path.get_file():
			return directory.get_file_import_is_valid(index)
	return false

func _initialize_main_screen() -> void:
	if not is_inside_tree() or is_instance_valid(main_screen): return
	if EditorInterface.get_resource_filesystem().is_scanning(): return
	# Plugins enter before the initial import; native documents require the imported font.
	if not _import_ready("res://addons/necronomidom/fonts/NotoSans-Regular.otf"): return
	main_screen = MainScreen.new()
	main_screen.visible = _visible_requested
	EditorInterface.get_editor_main_screen().add_child(main_screen)

func _exit_tree() -> void:
	var filesystem = EditorInterface.get_resource_filesystem()
	if filesystem.filesystem_changed.is_connected(_initialize_main_screen):
		filesystem.filesystem_changed.disconnect(_initialize_main_screen)
	if is_instance_valid(main_screen):
		main_screen.get_parent().remove_child(main_screen)
		main_screen.queue_free()
	main_screen = null

func _disable_plugin() -> void:
	# Ordinary editor exit must not rewrite project.godot or remove its autoloads.
	remove_autoload_singleton(OVERLAY_NAME)
	remove_autoload_singleton(AUTOLOAD_NAME)

static func needs_autoload(name: String) -> bool:
	return not ProjectSettings.has_setting("autoload/" + name)

func _has_main_screen() -> bool:
	return true

func _get_plugin_name() -> String:
	return "Dirge"

func _get_plugin_icon() -> Texture2D:
	if _import_ready(ICON_PATH): return load(ICON_PATH)
	return EditorInterface.get_editor_theme().get_icon("AudioStreamPlayer", "EditorIcons")

func _make_visible(value: bool) -> void:
	_visible_requested = value
	if is_instance_valid(main_screen): main_screen.visible = value
