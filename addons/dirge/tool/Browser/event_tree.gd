@tool
extends RefCounted

static func build(events: Array, extra_folders: Array = [], filter: String = "", tag: String = "") -> Dictionary:
	var root = _node("", "", "")
	var needle = filter.strip_edges().to_lower()
	if tag.is_empty():
		for folder in extra_folders:
			if not str(folder).is_empty() and (needle.is_empty() or str(folder).to_lower().contains(needle)):
				_ensure_folder(root, folder)
	for event in events:
		var path = str(event.get("path", ""))
		if not needle.is_empty() and not path.to_lower().contains(needle):
			continue
		if not tag.is_empty() and not event.get("tags", []).has(tag):
			continue
		var folder = folder_of(path)
		var parent = root if folder.is_empty() else _ensure_folder(root, folder)
		parent.children.append(_node(name_of(path), path, str(event.get("id", ""))))
	_sort(root)
	return root

static func all_tags(events: Array) -> Array[String]:
	var result: Array[String] = []
	for event in events:
		for tag in event.get("tags", []):
			if not result.has(str(tag)):
				result.append(str(tag))
	result.sort()
	return result

static func normalize_tag(raw: String) -> String:
	var whitespace = RegEx.new()
	whitespace.compile("\\s+")
	var tag = whitespace.sub(raw.strip_edges(), " ", true)
	return "" if tag.contains(",") else tag

static func folder_of(path: String) -> String:
	var slash = path.rfind("/")
	return "" if slash < 0 else path.substr(0, slash)

static func name_of(path: String) -> String:
	return path.substr(path.rfind("/") + 1)

static func join(folder: String, name: String) -> String:
	return name if folder.is_empty() else folder + "/" + name

static func is_under(path: String, folder: String) -> bool:
	return folder.is_empty() or path == folder or path.begins_with(folder + "/")

static func reparent(path: String, old_folder: String, new_folder: String) -> String:
	return join(new_folder, path if old_folder.is_empty() else path.substr(old_folder.length() + 1))

static func unique_path(events: Array, desired: String) -> String:
	var used: Dictionary = {}
	for event in events:
		used[event.get("path", "")] = true
	var candidate = desired
	var suffix = 2
	while used.has(candidate):
		candidate = desired + " " + str(suffix)
		suffix += 1
	return candidate

static func is_valid_name(name: String) -> bool:
	return not name.strip_edges().is_empty() and not name.contains("/")

static func _node(name: String, path: String, id: String) -> Dictionary:
	return {"name": name, "full_path": path, "event_id": id, "is_folder": id.is_empty(), "children": []}

static func _ensure_folder(root: Dictionary, folder_path: String) -> Dictionary:
	var current = root
	var prefix = ""
	for segment in folder_path.split("/", true):
		prefix = join(prefix, segment)
		var found: Dictionary = {}
		for child in current.children:
			if child.is_folder and child.name == segment:
				found = child
				break
		if found.is_empty():
			found = _node(segment, prefix, "")
			current.children.append(found)
		current = found
	return current

static func _sort(node: Dictionary) -> void:
	node.children.sort_custom(func(a, b):
		if a.is_folder != b.is_folder:
			return a.is_folder
		return str(a.name).to_lower() < str(b.name).to_lower())
	for child in node.children:
		_sort(child)
