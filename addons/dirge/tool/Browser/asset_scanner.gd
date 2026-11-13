@tool
extends RefCounted

const SKIPPED_FOLDERS = ["addons", ".godot", ".git", "bin", "obj"]
var files: Array[String] = []
var _lengths: Dictionary = {}

func scan() -> Dictionary:
	files.clear()
	var root = _node("res://", "res://", true)
	_walk("res://", root)
	files.sort_custom(func(a, b): return a.to_lower() < b.to_lower())
	return root

static func is_audio_file(path: String) -> bool:
	return path.get_extension().to_lower() in ["wav", "ogg", "mp3"]

func length(path: String) -> float:
	if _lengths.has(path):
		return _lengths[path]
	var seconds: float = 0.0
	if ResourceLoader.exists(path):
		var stream = ResourceLoader.load(path)
		if stream is AudioStream:
			seconds = stream.get_length()
	_lengths[path] = seconds
	return seconds

static func filter(root: Dictionary, query: String) -> Dictionary:
	var copy = _node(root.name, root.path, true)
	for child in root.children:
		if child.is_folder:
			var folder = filter(child, query)
			if not folder.children.is_empty():
				copy.children.append(folder)
		elif str(child.path).to_lower().contains(query.to_lower()):
			copy.children.append(child)
	return copy

static func _node(name: String, path: String, folder: bool) -> Dictionary:
	return {"name": name, "path": path, "is_folder": folder, "children": []}

func _walk(directory: String, node: Dictionary) -> void:
	var dir = DirAccess.open(directory)
	if dir == null or dir.file_exists(".gdignore"):
		return
	dir.include_hidden = false
	dir.include_navigational = false
	if dir.list_dir_begin() != OK:
		return
	var folders: Array[String] = []
	var audio: Array[String] = []
	var name = dir.get_next()
	while not name.is_empty():
		if not dir.is_link(name):
			if dir.current_is_dir():
				if not name.begins_with(".") and not SKIPPED_FOLDERS.has(name):
					folders.append(name)
			elif is_audio_file(name):
				audio.append(name)
		name = dir.get_next()
	dir.list_dir_end()
	folders.sort_custom(func(a, b): return a.to_lower() < b.to_lower())
	audio.sort_custom(func(a, b): return a.to_lower() < b.to_lower())
	for folder in folders:
		var child = _node(folder, directory.path_join(folder), true)
		_walk(child.path, child)
		if not child.children.is_empty():
			node.children.append(child)
	for file in audio:
		var path = directory.path_join(file)
		node.children.append(_node(file, path, false))
		files.append(path)
