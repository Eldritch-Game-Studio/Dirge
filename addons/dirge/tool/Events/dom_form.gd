@tool
extends RefCounted

# Values and handlers are refreshed independently of structure. Unchanged forms retain
# their real DOM inputs, focus and scroll; only structural edits replace descendants.
var host
var prefix: String
var _serial: int = 0
var _handlers: Dictionary = {}
var _values: Dictionary = {}
var _texts: Dictionary = {}
var _classes: Dictionary = {}
var _markup: Dictionary = {}
var _old_values: Dictionary = {}
var _old_texts: Dictionary = {}
var _old_classes: Dictionary = {}
var _menu_actions: Dictionary = {}
var _root: String
var _built_handlers: Dictionary = {}

func _init(document_host, id_prefix: String) -> void:
	host = document_host
	prefix = id_prefix
	host.listen("change")
	host.listen("pointerdown")
	host.clicked.connect(_clicked)
	host.dom_event.connect(_event)

func begin(root: String = "content") -> void:
	_root = root
	_serial = 0
	_built_handlers = {}
	_values = {}
	_texts = {}
	_classes = {}

func _id() -> String:
	_serial += 1
	return prefix + "-" + _root + "-" + str(_serial)

static func escape(value) -> String:
	return str(value).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;").replace("'", "&#39;")

static func div(content: String, css: String = "") -> String:
	return '<div class="%s">%s</div>' % [escape(css), content]

static func span(text: String, css: String = "") -> String:
	return '<span class="%s">%s</span>' % [escape(css), escape(text)]

static func note(text: String) -> String:
	return '<p class="note">%s</p>' % escape(text)

static func section(title: String, content: String, css: String = "") -> String:
	return div(div(span(title, "section-title"), "section-head") + div(content, "section-body " + css), "section")

static func row(label: String, content: String) -> String:
	return div(span(label, "label") + content, "row")

func button(label: String, callback: Callable, css: String = "btn") -> String:
	var id = _id()
	_built_handlers[id] = {"click": callback}
	_texts[id] = label
	_classes[id] = css
	return '<button id="%s" class="btn"></button>' % id

func text_input(value: String, callback: Callable, placeholder: String = "", css: String = "field") -> String:
	var id = _id()
	_values[id] = value
	_classes[id] = css
	_built_handlers[id] = {"change": callback}
	return '<input id="%s" type="text" class="field" placeholder="%s">' % [id, escape(placeholder)]

func text(label: String, value: String, callback: Callable, placeholder: String = "") -> String:
	return row(label, text_input(value, callback, placeholder))

func number(label: String, value: float, callback: Callable, unit: String = "", integer: bool = false) -> String:
	var formatted = str(int(value)) if integer else String.num(value)
	var id = prefix + "-" + _root + "-" + str(_serial + 1)
	var commit_number = func(raw: String):
		var trimmed = raw.strip_edges()
		if (trimmed.is_valid_int() if integer else trimmed.is_valid_float()):
			var parsed = float(trimmed)
			if is_finite(parsed):
				callback.call(int(parsed) if integer else parsed)
				return
		host.set_value("#" + id, formatted)
	var input = text_input(formatted, commit_number, "", "field num")
	return row(label, input + (span(unit, "unit") if not unit.is_empty() else ""))

func toggle(label: String, value: bool, callback: Callable) -> String:
	return row(label, button("On" if value else "Off", func(): callback.call(not value), "toggle on" if value else "toggle"))

func choice(label: String, options: Array, selected: int, callback: Callable, empty: String = "(none)") -> String:
	return row(label, choice_button(options, selected, callback, empty))

func choice_button(options: Array, selected: int, callback: Callable, empty: String = "(none)") -> String:
	var id = _id()
	_texts[id] = str(options[selected]) if selected >= 0 and selected < options.size() else empty
	_classes[id] = "choice"
	_built_handlers[id] = {"click": func(): show_menu(id, options, selected, callback)}
	return '<button id="%s" class="choice"></button>' % id

func enum_choice(label: String, value: String, options: Array, callback: Callable) -> String:
	return choice(label, options, options.find(value), func(index): callback.call(options[index]))

func commit(markup: String) -> void:
	close_menu()
	var structural = _markup.get(_root, "") != markup
	if structural:
		host.set_inner_html("#" + _root, markup)
		_markup[_root] = markup
	for id in _values:
		if structural or _old_values.get(id) != _values[id]: host.set_value("#" + id, _values[id])
	for id in _texts:
		if structural or _old_texts.get(id) != _texts[id]: host.set_text("#" + id, _texts[id])
	for id in _classes:
		if structural or _old_classes.get(id) != _classes[id]: host.set_attribute("#" + id, "class", _classes[id])
	for id in _handlers.keys():
		if str(id).begins_with(prefix + "-" + _root + "-"): _handlers.erase(id)
	_handlers.merge(_built_handlers, true)
	_old_values.merge(_values, true)
	_old_texts.merge(_texts, true)
	_old_classes.merge(_classes, true)

func show_menu(anchor: String, options: Array, selected: int, callback: Callable) -> void:
	close_menu()
	var content = ""
	for index in options.size():
		var id = prefix + "-menu-" + str(index)
		content += '<button id="%s" class="menu-item%s">%s</button>' % [id, " on" if index == selected else "", escape(options[index])]
		_menu_actions[id] = func(): callback.call(index)
	if options.is_empty(): content = span("(nothing to pick)", "menu-empty")
	host.append_html("body", '<div id="dom-menu" class="menu">%s</div>' % content)
	var rect: Rect2 = host.get_element_rect("#" + anchor)
	host.set_style("#dom-menu", "left", String.num(maxf(0, rect.position.x)) + "px")
	host.set_style("#dom-menu", "top", String.num(maxf(0, rect.end.y)) + "px")

func close_menu() -> void:
	if host.has_element("#dom-menu"): host.remove_element("#dom-menu")
	_menu_actions.clear()

func _clicked(id: String) -> void:
	if _menu_actions.has(id):
		var callback: Callable = _menu_actions[id]
		close_menu()
		callback.call()
	elif _handlers.has(id) and _handlers[id].has("click"):
		_handlers[id].click.call()

func _event(type: String, id: String) -> void:
	if type == "pointerdown" and not _menu_actions.has(id): close_menu()
	if type == "change" and _handlers.has(id) and _handlers[id].has("change"):
		_handlers[id].change.call(host.get_value("#" + id))

func dispose() -> void:
	if is_instance_valid(host):
		if host.clicked.is_connected(_clicked): host.clicked.disconnect(_clicked)
		if host.dom_event.is_connected(_event): host.dom_event.disconnect(_event)
	_handlers.clear()
	_built_handlers.clear()
	_menu_actions.clear()
