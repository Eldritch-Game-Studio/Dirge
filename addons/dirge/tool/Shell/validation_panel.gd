@tool
extends Control
const Docs = preload("res://addons/dirge/tool/Core/tool_documents.gd")
const Subjects = preload("res://addons/dirge/tool/Shell/subject_lookup.gd")
signal counts_changed(errors: int, warnings: int)
signal subject_selected(subject: Dictionary)
var document: RefCounted
var host: Control
var diagnostics: Array = []
var error_count: int = 0
var warning_count: int = 0
var _debounce: float = -1.0
var _selected: int = -1

func setup(value: RefCounted) -> void:
	document = value
	host = Docs.create("validation.html")
	var slot = Docs.slot(host)
	add_child(slot)
	slot.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	host.connect("clicked", _clicked)
	document.changed.connect(_changed)
	refresh(true)

func _changed() -> void:
	_debounce = 0.5

func _process(delta: float) -> void:
	if _debounce < 0.0: return
	_debounce -= delta
	if _debounce <= 0.0:
		_debounce = -1.0
		refresh()

func _exit_tree() -> void:
	if document != null and document.changed.is_connected(_changed): document.changed.disconnect(_changed)

func refresh(force: bool = false) -> void:
	var latest = document.validate()
	if not force and latest == diagnostics: return
	diagnostics = latest
	error_count = 0
	warning_count = 0
	var html = ""
	for index in diagnostics.size():
		var diagnostic = diagnostics[index]
		var error = str(diagnostic.severity).to_lower() == "error"
		if error: error_count += 1
		else: warning_count += 1
		var subject = Subjects.resolve(document.project, str(diagnostic.get("subject", "")))
		html += '<div id="diagnostic-%d" class="diagnostic %s%s" role="listitem"><p class="severity">%s</p><p class="code">%s</p><p class="message">%s</p><p class="subject">%s</p></div>' % [index, "error" if error else "warning", " unselectable" if subject.is_empty() else "", "ERROR" if error else "WARNING", Docs.escape(diagnostic.code), Docs.escape(diagnostic.message), Docs.escape(subject.get("name", "project"))]
	host.call("set_inner_html", "#list", html)
	_selected = -1
	host.call("set_text", "#summary", "No issues" if diagnostics.is_empty() else "%d errors · %d warnings" % [error_count, warning_count])
	Docs.hidden(host, "#empty", not diagnostics.is_empty())
	Docs.hidden(host, "#list", diagnostics.is_empty())
	counts_changed.emit(error_count, warning_count)

func _clicked(id: String) -> void:
	if id == "revalidate": refresh(true)
	elif id.begins_with("diagnostic-"):
		var index = int(id.trim_prefix("diagnostic-"))
		if index < 0 or index >= diagnostics.size(): return
		_select(index)
		var subject = Subjects.resolve(document.project, str(diagnostics[index].get("subject", "")))
		if not subject.is_empty(): subject_selected.emit(subject)

func select_subject(id: String) -> void:
	for index in diagnostics.size():
		if diagnostics[index].get("subject") == id:
			_select(index)
			return
	_select(-1)

func _select(index: int) -> void:
	if _selected == index: return
	if _selected >= 0: Docs.class_state(host, "#diagnostic-" + str(_selected), "selected", false)
	_selected = index
	if index >= 0: Docs.class_state(host, "#diagnostic-" + str(index), "selected", true)
