@tool
extends RefCounted

var host: Node
var dialog: FileDialog
var pending: Callable

func _init(parent: Node) -> void:
	host = parent

func pick(current: String, picked: Callable) -> void:
	pending = picked
	if not is_instance_valid(dialog):
		dialog = FileDialog.new()
		dialog.file_mode = FileDialog.FILE_MODE_OPEN_FILE
		dialog.access = FileDialog.ACCESS_RESOURCES
		dialog.filters = PackedStringArray(["*.wav ; WAV", "*.ogg ; Ogg Vorbis", "*.mp3 ; MP3", "*.tres,*.res ; AudioStream resource"])
		dialog.title = "Pick audio asset"
		host.add_child(dialog)
		dialog.file_selected.connect(_on_file)
		dialog.canceled.connect(func(): pending = Callable())
	if not current.is_empty(): dialog.current_path = current
	dialog.popup_centered_ratio(0.6)

func _on_file(path: String) -> void:
	var callback = pending
	pending = Callable()
	if callback.is_valid(): callback.call(path)

func dispose() -> void:
	if is_instance_valid(dialog): dialog.queue_free()
	pending = Callable()
