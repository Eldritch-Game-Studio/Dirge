@tool
extends RefCounted
const Model = preload("res://addons/dirge/model/Project/project_model.gd")

# Only ID-typed properties are rewritten: authored names, tags and asset strings
# can legitimately equal an ID and must not be changed.
const ID_KEYS = ["id", "owner", "bus", "parameter", "event", "snapshot", "destination", "targetEvent", "targetParameter"]
const ID_LIST_KEYS = ["events", "vcas"]

static func clone(project: Dictionary, source: Dictionary, new_path: String) -> Dictionary:
	var found: bool = false
	for event in project.get("events", []):
		if event.get("id") == source.get("id"):
			found = true
			break
	if not found:
		push_error("Event is not part of the project.")
		return {}
	var result = source.duplicate(true)
	reassign_ids(result)
	result["path"] = new_path
	return result

static func reassign_ids(event: Dictionary) -> Dictionary:
	var ids: Dictionary = {}
	_collect(event, ids)
	_remap(event, ids)
	return ids

static func _collect(node: Variant, ids: Dictionary) -> void:
	if node is Dictionary:
		var id = str(node.get("id", ""))
		if not id.is_empty() and id != Model.NONE and not ids.has(id):
			ids[id] = Model.new_id()
		for value in node.values():
			if value is Dictionary or value is Array:
				_collect(value, ids)
	elif node is Array:
		for value in node:
			_collect(value, ids)

static func _remap(node: Variant, ids: Dictionary, parent_key: String = "") -> void:
	if node is Dictionary:
		for key in node:
			var value = node[key]
			if value is String and ID_KEYS.has(key) and ids.has(value):
				node[key] = ids[value]
			elif value is Dictionary or value is Array:
				_remap(value, ids, key)
	elif node is Array:
		for index in node.size():
			var value = node[index]
			if value is String and ID_LIST_KEYS.has(parent_key) and ids.has(value):
				node[index] = ids[value]
			else:
				_remap(value, ids)
