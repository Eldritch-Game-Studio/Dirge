@tool
extends RefCounted

const ProjectJson = preload("res://addons/dirge/model/Serialization/project_json.gd")
const Validator = preload("res://addons/dirge/model/Validation/project_validator.gd")
const Model = preload("res://addons/dirge/model/Project/project_model.gd")

enum ChangeScope { NONE = 0, MIXER = 1, SNAPSHOTS = 2, GLOBAL_PARAMETERS = 4, EVENTS = 8, EVENT_CONTENT = 16, ASSETS = 32, ALL = 63 }
signal changed
signal scoped_changed(scope: int, subject: String)
signal saved

var project: Dictionary
var path: String = ""
var dirty: bool = false
var changed_scope: int = ChangeScope.ALL
var changed_subject: String = ""
var _done: Array[Dictionary] = []
var _undone: Array[Dictionary] = []
var _merge_token: Variant = null
var _transaction_depth: int = 0

var can_undo: bool:
	get: return not _done.is_empty()
var can_redo: bool:
	get: return not _undone.is_empty()
var undo_label: String:
	get: return str(_done.back().label) if can_undo else ""
var redo_label: String:
	get: return str(_undone.back().label) if can_redo else ""

func _init() -> void:
	project = new_project()

static func new_project() -> Dictionary:
	var result = Model.new_project("Untitled")
	result.mixer.buses.append(Model.create("Bus", {"name": "Master"}))
	return result

func replace(value: Dictionary, file_path: String = "") -> void:
	project = value.duplicate(true)
	path = file_path
	dirty = false
	_done.clear()
	_undone.clear()
	end_continuous()
	_notify(ChangeScope.ALL, "")

func reset() -> void:
	replace(new_project())

func load_text(text: String, file_path: String = "") -> Error:
	var parser = JSON.new()
	if parser.parse(text) != OK or not parser.data is Dictionary:
		return ERR_PARSE_ERROR
	var parsed = ProjectJson.deserialize(text)
	if parsed.is_empty():
		return ERR_PARSE_ERROR
	replace(parsed, file_path)
	return OK

func load_file(file_path: String) -> Error:
	var file = FileAccess.open(file_path, FileAccess.READ)
	if file == null:
		return FileAccess.get_open_error()
	return load_text(file.get_as_text(), file_path)

func to_text() -> String:
	return ProjectJson.serialize(project)

func save(file_path: String = "") -> Error:
	var target = path if file_path.is_empty() else file_path
	if target.is_empty():
		return ERR_INVALID_PARAMETER
	var error = ProjectJson.save_file(target, project)
	if error == OK:
		mark_saved(target)
	return error

func mark_saved(file_path: String) -> void:
	path = file_path
	dirty = false
	end_continuous()
	saved.emit()

func validate() -> Array:
	return Validator.validate(project)

## Nested edits are part of their outer transaction; only that transaction emits.
func transact(label: String, edit: Callable, scope: int = ChangeScope.ALL, subject: String = "") -> void:
	if _transaction_depth > 0:
		edit.call()
		return
	var before = project.duplicate(true)
	_transaction_depth += 1
	edit.call()
	_transaction_depth -= 1
	_done.append({"label": label, "before": before, "after": project.duplicate(true), "scope": scope, "subject": subject})
	_undone.clear()
	end_continuous()
	dirty = true
	_notify(scope, subject)

## A drag retains its initial snapshot and latest value until explicitly ended.
func transact_continuous(token: Variant, label: String, edit: Callable, scope: int = ChangeScope.ALL, subject: String = "") -> void:
	if token == null or _merge_token != token or _done.is_empty():
		transact(label, edit, scope, subject)
		_merge_token = token
		return
	edit.call()
	_done.back()["after"] = project.duplicate(true)
	_undone.clear()
	dirty = true
	_notify(scope, subject)

func end_continuous() -> void:
	_merge_token = null

func undo() -> bool:
	if not can_undo:
		return false
	var entry = _done.pop_back()
	project = entry.before.duplicate(true)
	_undone.append(entry)
	end_continuous()
	dirty = true
	_notify(entry.scope, entry.subject)
	return true

func redo() -> bool:
	if not can_redo:
		return false
	var entry = _undone.pop_back()
	project = entry.after.duplicate(true)
	_done.append(entry)
	end_continuous()
	dirty = true
	_notify(entry.scope, entry.subject)
	return true

func _notify(scope: int, subject: String) -> void:
	changed_scope = scope
	changed_subject = subject
	changed.emit()
	scoped_changed.emit(scope, subject)
